//            Copyright Keysight Technologies 2012-2019
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at http://mozilla.org/MPL/2.0/.
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Tap.Shared;

namespace OpenTap.Package
{
    /// <summary>
    /// Implements a IPackageRepository that queries a server for OpenTAP packages via http/https.
    /// </summary>
    public class HttpPackageRepository : IPackageRepository, IPackageDownloadProgress
    {
        private static TraceSource log = Log.CreateSource("HttpPackageRepository");
        private const string ApiVersion = "3.0";
        private VersionSpecifier MinRepoVersion = new VersionSpecifier(3, 0, 0, "", "", VersionMatchBehavior.AnyPrerelease | VersionMatchBehavior.Compatible);
        private string defaultUrl;
        private static HttpClient httpClient;
        private static HttpClient HttpClient
        {
            get
            {
                if (httpClient is null)
                {
                    httpClient = new HttpClient();
                    httpClient.DefaultRequestHeaders.Add("OpenTAP", PluginManager.GetOpenTapAssembly().SemanticVersion.ToString());
                    httpClient.DefaultRequestHeaders.Add(HttpRequestHeader.Accept.ToString(), "application/xml");
                }
                return httpClient;
            }
        }

        /// <summary>
        /// If true, most warnings will be logged as debug messages
        /// </summary>
        public bool IsSilent;
        private SemanticVersion _version;

        /// <summary>
        /// Get or set the version of the repository
        /// </summary>
        public SemanticVersion Version
        {
            get
            {
                if (_version == null)
                    CheckRepoApiVersion();

                return _version;
            }
            private set
            {
                _version = value;
            }
        }

        /// <summary>
        /// Initialize a http repository with the given URL
        /// </summary>
        /// <param name="url"></param>

        public HttpPackageRepository(string url)
        {
            url = url.Trim();
            if (Regex.IsMatch(url, "http(s)?://"))
                this.Url = url;
            else
                this.Url = "http://" + url;

            // Trim end to fix redirection. E.g. 'packages.opentap.io/' redirects to 'packages.opentap.io'.
            this.Url = this.Url.TrimEnd('/');
            defaultUrl = this.Url;
            this.Url = CheckUrlRedirect(this.Url);

            // Get the users Uniquely generated id
            var id = GetUserId();

            string installDir = ExecutorClient.ExeDir;
            UpdateId = String.Format("{0:X8}{1:X8}", MurMurHash3.Hash(id), MurMurHash3.Hash(installDir));
        }

        Action<string, long, long> IPackageDownloadProgress.OnProgressUpdate { get; set; }
        internal static string GetUserId()
        {
            var idPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "OpenTap", "OpenTapGeneratedId");
            string id = default(Guid).ToString(); // 00000000-0000-0000-0000-000000000000

            try
            {
                if (File.Exists(idPath))
                    id = File.ReadAllText(idPath);
                else
                {
                    id = Guid.NewGuid().ToString();
                    if (Directory.Exists(Path.GetDirectoryName(idPath)) == false)
                        Directory.CreateDirectory(Path.GetDirectoryName(idPath));
                    File.WriteAllText(idPath, id);
                }
            }
            catch (Exception e)
            {
                log.Error("Could not read user id.");
                log.Debug(e);
            }

            return id;
        }

        async Task DoDownloadPackage(PackageDef package, FileStream fileStream, CancellationToken cancellationToken)
        {
            bool finished = false;
            try
            {
                using (HttpClientHandler hch = new HttpClientHandler() { UseProxy = true, Proxy = WebRequest.GetSystemWebProxy() })
                using (HttpClient hc = new HttpClient(hch) { Timeout = Timeout.InfiniteTimeSpan })
                {
                    HttpResponseMessage response = null;
                    hc.DefaultRequestHeaders.Add("OpenTAP", PluginManager.GetOpenTapAssembly().SemanticVersion.ToString());
                    var retries = 60;
                    var downloadedBytes = 0;
                    var totalSize = -1L;

                    while (retries > 0)
                    {
                        if (retries < 60)
                            log.Debug($"Retrying {61 - retries}/60");

                        hc.DefaultRequestHeaders.Range = RangeHeaderValue.Parse($"bytes={downloadedBytes}-");

                        try
                        {
                            if (package.PackageSource is HttpRepositoryPackageDefSource httpSource && string.IsNullOrEmpty(httpSource.DirectUrl) == false)
                            {
                                log.Info($"Downloading package directly from: '{httpSource.DirectUrl}'.");
                                var message = new HttpRequestMessage(HttpMethod.Get, new Uri(httpSource.DirectUrl));

                                try
                                {
                                    response = await hc.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                                    if (response.IsSuccessStatusCode == false)
                                        throw new Exception($"Request to '{httpSource.DirectUrl}' failed with status code: {response.StatusCode}.");
                                }
                                catch (Exception e)
                                {
                                    log.Warning($"Could not download package directly from: '{httpSource.DirectUrl}'. Downloading package normally.");
                                    log.Debug(e);
                                    response = await hc.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                                }
                            }
                            else
                            {
                                var message = new HttpRequestMessage(HttpMethod.Get,
                                    new Uri(Url + "/" + ApiVersion + "/DownloadPackage" +
                                          $"/{Uri.EscapeDataString(package.Name)}" +
                                          $"?version={Uri.EscapeDataString(package.Version.ToString())}" +
                                          $"&os={Uri.EscapeDataString(package.OS)}" +
                                          $"&architecture={Uri.EscapeDataString(package.Architecture.ToString())}"));
                                response = await hc.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                            }

                            if (totalSize < 0)
                                totalSize = response.Content.Headers.ContentLength ?? 1;

                            // Download the package
                            using (var responseStream = await response.Content.ReadAsStreamAsync())
                            {
                                if (response.IsSuccessStatusCode == false)
                                    throw new HttpRequestException($"The download request failed with {response.StatusCode}.");

                                var buffer = new byte[4096];
                                int read = 0;

                                var task = Task.Run(() =>
                                {
                                    do
                                    {
                                        read = responseStream.Read(buffer, 0, 4096);
                                        fileStream.Write(buffer, 0, read);
                                        downloadedBytes += read;
                                    } while (read > 0);

                                    finished = true;
                                }, cancellationToken);
                                ConsoleUtils.ReportProgressTillEnd(task, "Downloading",
                                    () => fileStream.Position,
                                    () => totalSize,
                                    (header, pos, len) =>
                                    {
                                        ConsoleUtils.printProgress(header, pos, len);
                                        (this as IPackageDownloadProgress).OnProgressUpdate?.Invoke(header, pos, len);
                                    });
                            }

                            if (finished)
                                break;
                        }
                        catch (Exception e)
                        {
                            response.Dispose();
                            retries--;
                            if (retries <= 0 || cancellationToken.IsCancellationRequested)
                                throw;
                            log.Debug("Failed to download package.");
                            log.Debug(e);
                            Thread.Sleep(TimeSpan.FromSeconds(1));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is TaskCanceledException))
                {
                    throw;
                }
                log.Error(ex);
            }
        }

        private string downloadPackagesString(string args, string data = null, string contentType = null, string accept = null)
        {
            string xmlText = null;
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Proxy = WebRequest.GetSystemWebProxy();
                    wc.Headers.Add(HttpRequestHeader.Accept, accept ?? "application/xml");
                    wc.Headers.Add("OpenTAP", PluginManager.GetOpenTapAssembly().SemanticVersion.ToString());
                    wc.Encoding = Encoding.UTF8;

                    if (data != null)
                    {
                        wc.Headers[HttpRequestHeader.ContentType] = contentType ?? "application/x-www-form-urlencoded";
                        xmlText = wc.UploadString(Url + args, "POST", data);
                    }
                    else
                    {
                        xmlText = wc.DownloadString(Url + args);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is WebException)
                    CheckRepoApiVersion();

                var exception = new WebException("Error communicating with repository at '" + defaultUrl + "'.", ex);

                if (!IsSilent)
                    log.Warning("Error communicating with repository at '{0}' : {1}.", defaultUrl, ex.Message);
                else
                    log.Debug(exception);

                throw exception;
            }
            return xmlText;
        }
        private string CheckUrlRedirect(string url)
        {
            try
            {
                try
                {
                    var versionUrl = $"{url}/{ApiVersion}/version";
                    var response = HttpClient.GetAsync(versionUrl).Result;

                    // Check for http server redirects
                    url = checkServerRedirect(url, versionUrl, response);

                    // Check client redirects
                    var xmlText = response.Content.ReadAsStringAsync().Result;
                    url = checkClientRedirect(url, xmlText);
                }
                catch
                {
                    try
                    {
                        var xmlText = HttpClient.GetStringAsync(url).Result;
                        url = checkClientRedirect(url, xmlText);
                    }
                    catch
                    {
                        return url;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Debug(ex);
            }
            return url.TrimEnd('/');
        }

        private string checkServerRedirect(string url, string versionUrl, HttpResponseMessage response)
        {
            var redirectedUrl = response.RequestMessage.RequestUri.ToString();
            if (versionUrl != redirectedUrl)
            {
                redirectedUrl = new HttpClient().GetAsync(url).Result.RequestMessage.RequestUri.ToString();
                log.Debug($"Redirected from '{url}' to '{redirectedUrl}'.");
                url = redirectedUrl;
            }

            return url;
        }

        private string checkClientRedirect(string url, string xmlText)
        {
            try
            {
                var match = Regex.Match(xmlText, "<meta.*?http-equiv=\\\"refresh\\\".*?>");
                if (match.Success)
                {
                    log.Debug("Found redirect in repository URL. Redirecting to new URL...");
                    match = Regex.Match(match.Value, "url=(.*?)(?:\\\"|')");
                    if (match.Success)
                        url = CheckUrlRedirect(match.Groups[1].Value);
                }
            }
            catch { }

            return url;
        }

        private void CheckRepoApiVersion()
        {
            string tryDownload(string url)
            {
                try { return HttpClient.GetStringAsync(url).Result; }
                catch { return null; }
            }

            // Check specific version
            var data = tryDownload($"{Url}/{ApiVersion}/version");

            if (string.IsNullOrEmpty(data))
            {
                // Url does not exists
                if (tryDownload(Url) == null)
                    throw new WebException($"Unable to connect to '{defaultUrl}'.");

                // Check old repo
                if (tryDownload($"{Url}/2.0/version") != null)
                    throw new NotSupportedException($"The repository '{defaultUrl}' is only compatible with TAP 8.x or ealier.");

                throw new NotSupportedException($"'{defaultUrl}' is not a package repository.");
            }

            var reader = XmlReader.Create(new StringReader(data));
            var serializer = new XmlSerializer(typeof(string));
            if (serializer.CanDeserialize(reader) == false)
                throw new NotSupportedException($"'{defaultUrl}' is not a package repository.");
            var version = serializer.Deserialize(reader) as string;
            if (SemanticVersion.TryParse(version, out _version) && MinRepoVersion.IsCompatible(_version) == false)
                throw new NotSupportedException($"The repository '{defaultUrl}' is not supported.", new Exception($"Repository version '{Version}' is not compatible with min required version '{MinRepoVersion}'."));
        }

        PackageDef[] packagesFromXml(string xmlText)
        {
            try
            {
                if (string.IsNullOrEmpty(xmlText) || xmlText == "null") return new PackageDef[0];
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xmlText)))
                {
                    var packages = PackageDef.ManyFromXml(stream).ToArray();
                    packages.ForEach(p =>
                    {
                        if (p.PackageSource == null)
                            p.PackageSource = new HttpRepositoryPackageDefSource
                            {
                                RepositoryUrl = Url
                            };
                    });

                    return packages;
                }
            }
            catch (XmlException ex)
            {
                if (!IsSilent)
                    log.Warning("Invalid xml from package repository at '{0}'.", defaultUrl);
                else
                    log.Debug("Invalid xml from package repository at '{0}'.", defaultUrl);
                log.Debug(ex);
                log.Debug("Redirected url '{0}'", Url);
                log.Debug(xmlText);
            }
            catch (Exception ex)
            {
                if (!IsSilent)
                    log.Warning("Error reading from package repository at '{0}'.", defaultUrl);
                else
                    log.Debug("Error reading from package repository at '{0}'.", defaultUrl);
                log.Debug(ex);
                log.Debug("Redirected url '{0}'", Url);
            }
            return new PackageDef[0];
        }

        private PackageDef[] ConvertToPackageDef(IPackageIdentifier[] packages)
        {
            return packages.Select(p => new PackageDef()
            {
                Name = p.Name,
                Version = p.Version,
                Architecture = p.Architecture,
                OS = p.OS
            }).ToArray();
        }

        private IPackageIdentifier[] CheckCompatibleWith(IPackageIdentifier[] compatibleWith)
        {
            if (compatibleWith == null)
                return null;

            var list = compatibleWith.ToList();

            var openTap = compatibleWith.FirstOrDefault(p => p.Name == "OpenTAP");
            if (openTap != null)
            {
                list.AddRange(new[]
                {
                    new PackageIdentifier("Tap", openTap.Version, openTap.Architecture, openTap.OS),
                    new PackageIdentifier("TAP Base", openTap.Version, openTap.Architecture, openTap.OS)
                });
            }

            return list.ToArray();
        }

        #region IPackageRepository Implementation

        /// <summary>
        /// Get the URL of the repository
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// Download a package to a specific destination
        /// </summary>
        /// <param name="package"></param>
        /// <param name="destination"></param>
        /// <param name="cancellationToken"></param>
        public void DownloadPackage(IPackageIdentifier package, string destination, CancellationToken cancellationToken)
        {
            var tmpPath = destination + "." + Guid.NewGuid().ToString();
            //Use DeleteOnClose to auto-magically remove the file when the stream or application is closed.
            using (var tmpFile = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite,
                FileShare.Delete | FileShare.Read, 4096, FileOptions.DeleteOnClose))
            {

                try
                {
                    var packageDef = package as PackageDef ?? new PackageDef
                    {
                        Name = package.Name,
                        Version = package.Version,
                        Architecture = package.Architecture,
                        OS = package.OS
                    };

                    DoDownloadPackage(packageDef, tmpFile, cancellationToken).Wait(cancellationToken);

                    if (cancellationToken.IsCancellationRequested == false)
                    {
                        tmpFile.Flush();
                        File.Delete(destination);
                        File.Copy(tmpFile.Name, destination);
                    }
                }
                catch
                {
                    log.Warning("Download failed.");
                    throw;
                }
                finally
                {
                    File.Delete(tmpFile.Name);
                }
            }
        }

        /// <summary>
        /// Get the names of the available packages in the repository
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <param name="compatibleWith"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public string[] GetPackageNames(CancellationToken cancellationToken, params IPackageIdentifier[] compatibleWith)
        {
            string response;

            var arg = string.Format("/{0}/GetPackageNames", ApiVersion);
            if (compatibleWith == null || compatibleWith.Length == 0)
                response = downloadPackagesString(arg);
            else
            {
                using (Stream stream = new MemoryStream())
                {
                    compatibleWith = CheckCompatibleWith(compatibleWith);
                    PackageDef.SaveManyTo(stream, ConvertToPackageDef(compatibleWith));
                    stream.Seek(0, 0);
                    string data = new StreamReader(stream).ReadToEnd();

                    cancellationToken.ThrowIfCancellationRequested();

                    response = downloadPackagesString(arg, data, "application/xml");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(response)))
                using (var tr = new StreamReader(ms))
                {
                    var root = XElement.Load(tr);
                    return root.Nodes().OfType<XElement>().Select(e => e.Value).ToArray();
                }
            }
            catch (XmlException)
            {
                log.Debug("Redirected url '{0}'", Url);
                log.Debug(response);

                throw new Exception($"Invalid xml from package repository at '{defaultUrl}'.");
            }
        }

        /// <summary>
        /// Get the names of the available packages in the repository with the specified class
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public string[] GetPackageNames(string @class, CancellationToken cancellationToken, params IPackageIdentifier[] compatibleWith)
        {
            string response;

            var arg = string.Format("/{0}/GetPackageNames?class={1}", ApiVersion, Uri.EscapeDataString(@class));
            if (compatibleWith == null || compatibleWith.Length == 0)
                response = downloadPackagesString(arg);
            else
            {
                using (Stream stream = new MemoryStream())
                {
                    compatibleWith = CheckCompatibleWith(compatibleWith);
                    PackageDef.SaveManyTo(stream, ConvertToPackageDef(compatibleWith));
                    stream.Seek(0, 0);
                    string data = new StreamReader(stream).ReadToEnd();

                    cancellationToken.ThrowIfCancellationRequested();

                    response = downloadPackagesString(arg, data, "application/xml");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(response)))
                using (var tr = new StreamReader(ms))
                {
                    var root = XElement.Load(tr);
                    return root.Nodes().OfType<XElement>().Select(e => e.Value).ToArray();
                }
            }
            catch (XmlException)
            {
                log.Debug("Redirected url '{0}'", Url);
                log.Debug(response);

                throw new Exception($"Invalid xml from package repository at '{defaultUrl}'.");
            }
        }

        /// <summary>
        /// Get the available versions of packages with name 'packageName' and optionally compatible with a list of packages
        /// </summary>
        /// <param name="packageName"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="compatibleWith"></param>
        /// <returns></returns>
        public PackageVersion[] GetPackageVersions(string packageName, CancellationToken cancellationToken, params IPackageIdentifier[] compatibleWith)
        {
            string response;
            string arg = string.Format("/{0}/GetPackageVersions/{1}", ApiVersion, Uri.EscapeDataString(packageName));

            if (compatibleWith == null || compatibleWith.Length == 0)
                response = downloadPackagesString(arg);
            else
            {
                using (Stream stream = new MemoryStream())
                {
                    compatibleWith = CheckCompatibleWith(compatibleWith);
                    PackageDef.SaveManyTo(stream, ConvertToPackageDef(compatibleWith));
                    stream.Seek(0, 0);
                    string data = new StreamReader(stream).ReadToEnd();

                    cancellationToken.ThrowIfCancellationRequested();

                    response = downloadPackagesString(arg, data, "application/xml");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            var pkgs = new TapSerializer().DeserializeFromString(response, type: TypeData.FromType(typeof(PackageVersion[]))) as PackageVersion[];
            pkgs.AsParallel().ForAll(p => p.Name = packageName);
            return pkgs;
        }

        /// <summary>
        /// Get the available versions of packages matching 'package' and optionally compatible with a list of packages
        /// </summary>
        /// <param name="package"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="compatibleWith"></param>
        /// <returns></returns>
        public PackageDef[] GetPackages(PackageSpecifier package, CancellationToken cancellationToken, params IPackageIdentifier[] compatibleWith)
        {
            List<string> reqs = new List<string>();
            var endpoint = "/GetPackages";

            if (!string.IsNullOrWhiteSpace(package.Name)) endpoint = "/GetPackage/" + Uri.EscapeDataString(package.Name);

            if (!string.IsNullOrEmpty(package.Version.ToString()))
                reqs.Add(string.Format("version={0}", Uri.EscapeDataString(package.Version.ToString())));
            if (!string.IsNullOrWhiteSpace(package.OS))
                reqs.Add(string.Format("os={0}", Uri.EscapeDataString(package.OS)));
            if (package.Architecture != CpuArchitecture.AnyCPU)
                reqs.Add(string.Format("architecture={0}", Uri.EscapeDataString(package.Architecture.ToString())));

            // Check if package dependencies are compatible
            compatibleWith = CheckCompatibleWith(compatibleWith);
            foreach (var packageIdentifier in compatibleWith)
                reqs.Add(string.Format("compatibleWith={0}", Uri.EscapeDataString($"{packageIdentifier.Name}:{packageIdentifier.Version}")));


            if (reqs.Any())
                endpoint += "?" + string.Join("&", reqs);

            cancellationToken.ThrowIfCancellationRequested();

            return packagesFromXml(downloadPackagesString("/" + ApiVersion + endpoint));
        }

        /// <summary>
        /// Get Client ID
        /// </summary>
        public string UpdateId;

        private string PackageNameHash(string packageName)
        {
            using (System.Security.Cryptography.SHA256 algo = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = algo.ComputeHash(Encoding.UTF8.GetBytes(packageName));
                return BitConverter.ToString(hash).Replace("-", "");

            }
        }

        /// <summary>
        /// Query the repository for updated versions of specified packages
        /// </summary>
        /// <param name="packages"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public PackageDef[] CheckForUpdates(IPackageIdentifier[] packages, CancellationToken cancellationToken)
        {
            List<PackageDef> latestPackages = new List<PackageDef>();
            bool tempSilent = IsSilent;
            IsSilent = true;

            try
            {
                string response;

                using (Stream stream = new MemoryStream())
                {
                    PackageDef.SaveManyTo(stream, packages.Select(p => new PackageDef()
                    {
                        Name = PackageNameHash(p.Name),
                        Version = p.Version,
                        Architecture = p.Architecture,
                        OS = p.OS
                    }));

                    stream.Seek(0, 0);
                    string data = new StreamReader(stream).ReadToEnd();

                    string arg = string.Format("/{0}/CheckForUpdates?name={1}", ApiVersion, UpdateId);
                    response = downloadPackagesString(arg, data);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (response != null)
                    latestPackages = packagesFromXml(response).ToList();

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                log.Debug("Could not check for updates from package repository at '{0}'.", defaultUrl);
            }

            IsSilent = tempSilent;
            return latestPackages.ToArray();
        }
        #endregion

        /// <summary>
        /// Send the GraphQL query string to the repository.
        /// </summary>
        /// <param name="query">A GraphQL query string</param>
        /// <returns>A JObject containing the GraphQL response</returns>
        [Obsolete("Please use SendQuery or SendQueryAsync instead.")]
        public JObject Query(string query)
        {
            var response = downloadPackagesString($"/3.1/query", query, "application/json", "application/json");
            var json = JObject.Parse(response);
            return json;
        }

        /// <summary>
        /// Send the GraphQL query string to the repository.
        /// </summary>
        /// <param name="query">A GraphQL query string</param>
        /// <returns>A JSON string containing the GraphQL response</returns>
        public string QueryGraphQL(string query) =>
            downloadPackagesString($"/3.1/query", query, "application/json", "application/json");
    }
}

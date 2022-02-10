//            Copyright Keysight Technologies 2012-2019
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at http://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;

namespace OpenTap
{
    /// <summary>
    /// Class for getting user input without using GUI.
    /// </summary>
    public class UserInput
    {
        /// <summary> Request user input from the GUI. Waits an amount of time specified by Timeout. If the timeout occurs a TimeoutException will be thrown.</summary>
        /// <param name="dataObject">The object the user should fill out with data.</param>
        /// <param name="Timeout">How long to wait before timing out. </param>
        /// <param name="modal">set to True if a modal request is wanted. This means the user will have to answer before doing anything else.</param>
        public static void Request(object dataObject, TimeSpan Timeout, bool modal = false)
        {
            inputInterface?.RequestUserInput(dataObject, Timeout, modal);
        }

        /// <summary> Request user input from the GUI. Waits indefinitely.</summary>
        /// <param name="dataObject">The object the user should fill out with data.</param>
        /// <param name="modal">set to True if a modal request is wanted. This means the user will have to answer before doing anything else.</param>
        public static void Request(object dataObject, bool modal = false)
        {
            inputInterface?.RequestUserInput(dataObject, TimeSpan.MaxValue, modal);
        }

        /// <summary> Currently selected interface. </summary>
        public static object Interface => inputInterface;

        static readonly SessionLocal<IUserInputInterface> _inputInterface = new SessionLocal<IUserInputInterface>(false);
        static IUserInputInterface inputInterface => _inputInterface.Value;
        static IUserInterface userInterface => inputInterface as IUserInterface;
        /// <summary> Sets the current user input interface. This should almost never be called from user code. </summary>
        /// <param name="inputInterface"></param>
        public static void SetInterface(IUserInputInterface inputInterface)
        {
            _inputInterface.Value = inputInterface;
        }

        /// <summary> Call to notify the user interface that an object property has changed. </summary>
        /// <param name="obj"></param>
        /// <param name="property"></param>
        public static void NotifyChanged(object obj, string property)
        {
            userInterface?.NotifyChanged(obj, property);
        }

        /// <summary> Gets the current user input interface. </summary>
        /// <returns></returns>
        public static IUserInputInterface GetInterface() => inputInterface;
    }

    /// <summary> Defines a way for plugins to notify the user that a property has changed. </summary>
    public interface IUserInterface
    {
        /// <summary>
        /// This method is called to notify that a property has changed on an object.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="property"></param>
        void NotifyChanged(object obj, string property);
    }

    /// <summary> Defines a way for plugins to request input from the user. </summary>
    public interface IUserInputInterface
    {
        /// <summary> The method called when the interface requests user input.</summary>
        /// <param name="dataObject">The object the user should fill out with data.</param>
        /// <param name="Timeout">How long the user should have.</param>
        /// <param name="modal"> True if a modal request is wanted</param>
        void RequestUserInput(object dataObject, TimeSpan Timeout, bool modal);   
    }

    /// <summary> The supported layout modes. </summary>
    [Flags]
    public enum LayoutMode
    {
        /// <summary> The default mode.</summary>
        Normal = 1,
        /// <summary> The user input fills the whole row. </summary>
        FullRow = 2,
        /// <summary> The user input floats to the bottom.</summary>
        FloatBottom = 4
    }

    /// <summary> LayoutAttribute can be used to specify the wanted layout for user interfaces.</summary>
    public class LayoutAttribute : Attribute
    {
        /// <summary> Specifies the mode of layout.</summary>
        public LayoutMode Mode { get; }
        /// <summary> How much height should the input take.  </summary>
        public int RowHeight { get; }

        /// <summary> Maximum row height for the input. </summary>
        public int MaxRowHeight { get; } = 1000;

        /// <summary> </summary>
        /// <param name="mode"></param>
        /// <param name="rowHeight"></param>
        /// <param name="maxRowHeight"></param>
        public LayoutAttribute(LayoutMode mode, int rowHeight = 1, int maxRowHeight = 1000)
        {
            Mode = mode;
            RowHeight = rowHeight;
            MaxRowHeight = maxRowHeight;
        }
    }

    /// <summary> Specifies that a property finalizes input.</summary>
    public class SubmitAttribute : Attribute { }

    /// <summary> Standard implementation of UserInputInterface for Command Line interfaces</summary>
    public class CliUserInputInterface : IUserInputInterface
    {
        readonly Mutex userInputMutex = new Mutex();
        readonly object readerLock = new object();
        void IUserInputInterface.RequestUserInput(object dataObject, TimeSpan timeout, bool modal)
        {
            if(readerThread == null)
            {
                lock (readerLock)
                {
                    if (readerThread == null)
                    {
                        readerThread = TapThread.Start(() =>
                        {
                            try
                            {
                                var sb = new StringBuilder();
                                while (true)
                                {
                                    
                                    var chr = Console.ReadKey();
                                    var left = Console.CursorLeft;
                                    var top = Console.CursorTop;
                                    if (left > 0)
                                    {
                                        Console.SetCursorPosition(left - 1, top);
                                        Console.Write(" ");
                                    }

                                    if (chr.KeyChar == 0)
                                        continue;
                                    if (chr.Key == ConsoleKey.Enter)
                                    {
                                        lines.Add(sb.ToString());
                                        sb.Clear();
                                    }

                                    if (chr.Key == ConsoleKey.Backspace)
                                    {
                                        sb.Remove(sb.Length - 1, 1);
                                        Console.SetCursorPosition(left -1, top);
                                    }
                                    else
                                    {
                                        sb.Append(chr.KeyChar);
                                    }
                                }
                            }
                            catch(Exception e)
                            {
                                log.Error(e);
                            }
                        }, "Console Reader");
                    }
                }
            }
            TapThread.Sleep(1000);
            DateTime timeoutAt;
            if (timeout == TimeSpan.MaxValue)
                timeoutAt = DateTime.MaxValue;
            else
                timeoutAt = DateTime.Now + timeout;

            if (timeout >= new TimeSpan(0, 0, 0, 0, int.MaxValue))
                timeout = new TimeSpan(0, 0, 0, 0, -1);
            do
            {
                if (userInputMutex.WaitOne(timeout))
                    break;
                if (DateTime.Now >= timeoutAt)
                    throw new TimeoutException("Request User Input timed out");
            } while (true);

            try
            {
                
                var a = AnnotationCollection.Annotate(dataObject);
                var members = a.Get<IMembersAnnotation>()?.Members;

                if (members == null) return;
                members = members.Concat(a.Get<IForwardedAnnotations>()?.Forwarded ?? Array.Empty<AnnotationCollection>());

                // Order members
                members = members.OrderBy(m => m.Get<DisplayAttribute>()?.Name ?? m.Get<IMemberAnnotation>()?.Member.Name);
                members = members.OrderBy(m => m.Get<DisplayAttribute>()?.Order ?? -10000);
                members = members.OrderBy(m => m.Get<IMemberAnnotation>()?.Member?.GetAttribute<LayoutAttribute>()?.Mode == LayoutMode.FloatBottom ? 1 : 0);
                members = members.OrderBy(m => m.Get<IMemberAnnotation>()?.Member?.HasAttribute<SubmitAttribute>() == true ? 1 : 0);

                var display = a.Get<IDisplayAnnotation>();

                string title = null;
                if (display is DisplayAttribute attr && attr.IsDefaultAttribute() == false)
                    title = display.Name;
                
                // flush and make sure that there is no new log messages coming in (0.1s span).
                Log.Flush(TimeSpan.FromSeconds(0.1));    
                if (string.IsNullOrWhiteSpace(title))
                    // fallback magic
                    title = TypeData.GetTypeData(dataObject)?.GetMember("Name")?.GetValue(dataObject) as string;
                if (string.IsNullOrWhiteSpace(title) == false)
                {
                    Console.WriteLine(title);
                }
                bool isBrowsable(IMemberData m)
                {
                    var browsable = m.GetAttribute<System.ComponentModel.BrowsableAttribute>();

                    // Browsable overrides everything
                    if (browsable != null) return browsable.Browsable;

                    if (m is IMemberData mem)
                    {
                        if (m.HasAttribute<OutputAttribute>())
                            return true;
                        if (!mem.Writable || !mem.Readable)
                            return false;
                        return true;
                    }
                    return false;
                }
                foreach (var _message in members)
                {
                    var mem = _message.Get<IMemberAnnotation>()?.Member;
                    if (mem != null)
                    {
                        if (!isBrowsable(mem)) continue;
                    }
                    
                    bool secure = _message.Get<IReflectionAnnotation>()?.ReflectionInfo.DescendsTo(typeof(SecureString)) ?? false;
                    var str = _message.Get<IStringValueAnnotation>();
                    if (str == null && !secure) continue;
                    var name = _message.Get<DisplayAttribute>()?.Name;

                    start:
                    var isVisible = _message.Get<IAccessAnnotation>()?.IsVisible ?? true;
                    if (!isVisible) continue;


                    var isReadOnly = _message.Get<IAccessAnnotation>()?.IsReadOnly ?? false;
                    if (isReadOnly)
                    {
                        Console.WriteLine($"{str.Value}");
                        continue;
                    }

                    var proxy = _message.Get<IAvailableValuesAnnotationProxy>();
                    List<string> options = null;
                    bool pleaseEnter = true;
                    if (proxy != null)
                    {
                        pleaseEnter = false;
                        options = new List<string>();

                        int index = 0;
                        var current_value = proxy.SelectedValue;
                        foreach (var value in proxy.AvailableValues)
                        {
                            var v = value.Get<IStringValueAnnotation>();
                            if (v != null)
                            {

                                Console.Write("{1}: '{0}'", v.Value, index);
                                if (value == current_value)
                                {
                                    Console.WriteLine(" (default)");
                                }
                                else
                                {
                                    Console.WriteLine();
                                }
                            }
                            options.Add(v?.Value);
                            index++;
                        }
                        Console.Write("Please enter a number or name ");
                    }

                    var layout = _message.Get<IMemberAnnotation>()?.Member.GetAttribute<LayoutAttribute>();
                    bool showName = layout?.Mode.HasFlag(LayoutMode.FullRow) == true ? false : true;
                    if (pleaseEnter)
                    {
                        Console.Write("Please enter ");
                    }

                    if (secure && showName)
                    {
                        Console.Write($"{name}: ");
                    }else if (showName)
                        Console.Write($"{name} ({str.Value}): ");
                    else
                        Console.Write($"({str.Value}): ");
                    
                    
                    if (secure)
                    {
                        var read2 = (awaitReadLine(timeoutAt, true) ?? "").Trim();
                        _message.Get<IObjectValueAnnotation>().Value = read2.ToSecureString();
                        continue;

                    }

                    var read = (awaitReadLine(timeoutAt, false) ?? "").Trim();
                    if (read == "")
                    {
                        // accept the default value.
                        continue;
                    }
                    try
                    {

                        if (options != null && int.TryParse(read, out int result))
                        {
                            if (result < options.Count)
                                read = options[result];
                            else goto start;
                        }
                        str.Value = read;
                        
                        var err = a.Get<IErrorAnnotation>();
                        IEnumerable<string> errors = err?.Errors;

                        _message.Write();
                        if (errors?.Any() == true)
                        {
                            Console.WriteLine("Unable to parse value {0}", read);
                            goto start;
                        }

                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Unable to parse '{0}'", read);
                        goto start;
                    }
                }
                a.Write();
            }
            finally
            {
                userInputMutex.ReleaseMutex();
            }
        }

        /// <summary>
        /// AwaitReadline reads the line asynchronously.  
        /// This has to be done in a thread, otherwise we cannot abort the test plan in the meantime. </summary>
        /// <param name="TimeOut"></param>
        /// <returns></returns>
        string awaitReadLine(DateTime TimeOut, bool secure)
        {
            try
            {
                IsSecure = secure;
                while (DateTime.Now <= TimeOut)
                {
                    if (lines.TryTake(out string line, 20, TapThread.Current.AbortToken))
                        return line;
                }

                Console.WriteLine();
                log.Info("Timed out while waiting for user input.");
                throw new TimeoutException("Request user input timed out");
            }
            finally
            {
                IsSecure = false;
            }
        }

        TapThread readerThread = null;
        BlockingCollection<string> lines = new BlockingCollection<string>();
        
        static readonly SessionLocal<bool> isLoaded = new SessionLocal<bool>();
        
        /// <summary> Loads the CLI user input interface. Note, once it is loaded it cannot be unloaded. </summary>
        public static void Load()
        {
            if (!isLoaded.Value)
            {
                isLoaded.Value = true;
                UserInput.SetInterface(new CliUserInputInterface());
            }
        }

        static readonly TraceSource log = Log.CreateSource("UserInput");

        /// <summary>
        /// Acquires a lock on the user input requests, so that user inputs will have to
        /// wait for this object to be disposed in order to do the request.
        /// </summary>
        /// <returns>A disposable that must be disposed in the same thread as the caller.</returns>
        public static IDisposable AcquireUserInputLock()
        {
            if (isLoaded.Value && UserInput.Interface is CliUserInputInterface cli)
            {
                cli.userInputMutex.WaitOne();
                return Utils.WithDisposable(cli.userInputMutex.ReleaseMutex);
            }
            
            // when CliUserInputInterface is not being used we don't have to do this.
            return Utils.WithDisposable(Utils.Noop);
        }
    }
}

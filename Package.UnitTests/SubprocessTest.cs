﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenTap.Engine.UnitTests.TestTestSteps;
using OpenTap.Plugins.BasicSteps;

namespace OpenTap.Package.UnitTests
{
    [TestFixture]
    public class SubprocessTest
    {
        [Test]
        public void TestProcessIO()
        {
            var messages = new List<string>();

            var step = new LogStep
            {
                LogMessage = "Hello World"
            };

            var ph = new ProcessHelper(false);
            ph.MessageLogged += evts => messages.AddRange(evts.Select(e => e.Message));
            ph.Run(step, false, CancellationToken.None);

            CollectionAssert.Contains(messages, "Hello World");
        }

        [Test]
        public void TestSubprocessRun()
        {
            var verdicts = new[]
                { Verdict.Aborted, Verdict.Error, Verdict.Fail, Verdict.Inconclusive, Verdict.Pass, Verdict.NotSet };
            foreach (var ver in verdicts)
            {
                var step = new VerdictStep
                {
                    VerdictOutput = ver
                };
                var verdict = new ProcessHelper(false).Run(step, false, CancellationToken.None);
                Assert.AreEqual(ver, verdict);
            }
        }

        [Test]
        public void TestCancel()
        {
            var token = new CancellationTokenSource();

            var step = new DelayStep() { DelaySecs = 10 };
            var ph = new ProcessHelper(false);
            Task.Run(() => ph.Run(step, false, token.Token));

            // wait for process to start
            while (ph.LastProcessHandle == null)
                TapThread.Sleep(TimeSpan.FromMilliseconds(10));

            var sw = Stopwatch.StartNew();
            token.Cancel();
            ph.LastProcessHandle.WaitForExit();
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(3), "Process failed to exit in time.");
        }

        [Test]
        public void TestPipes()
        {
            var handle = "TestPipes";
            var server = new NamedPipeServerStream(handle, PipeDirection.InOut);
            var client = new NamedPipeClientStream(".", handle, PipeDirection.InOut);

            var conn = server.WaitForConnectionAsync();
            client.Connect();
            conn.GetAwaiter().GetResult();

            { // short message
                var testMessage = "Hello World";

                Task.Run(() => { client.WriteMessage(testMessage); });
                var res  = server.ReadMessage();
                var msg = Encoding.UTF8.GetString(res.ToArray());

                Assert.AreEqual(testMessage, msg);
            }
            { // many guids
                var testMessage = string.Join(" ", Enumerable.Repeat(Guid.NewGuid().ToString(), 1000));

                Task.Run(() => { client.WriteMessage(testMessage); });
                var res  = server.ReadMessage();
                var msg = Encoding.UTF8.GetString(res.ToArray());

                Assert.AreEqual(testMessage, msg);
            }
            { // random utf8 symbols
                var bytes = new byte[1 << 5];
                var rand = new Random(0);
                rand.NextBytes(bytes);

                var testMessage = Encoding.UTF8.GetString(bytes);

                Task.Run(() => { client.WriteMessage(testMessage); });
                var res  = server.ReadMessage();
                var msg = Encoding.UTF8.GetString(res.ToArray());

                Assert.AreEqual(testMessage, msg);
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO.Pipes;
using System.IO;
using System.Threading;
using System.Collections;
using System.Data;
using System.Data.SqlClient;
using System.Security.Principal;
using Microsoft.Win32;
using System.Diagnostics;

namespace Dzoka.AxSyncLibrary
{
    public class Server
    {
        private static int numThreads = 3;                          // count of pipe threads to launch
        private Thread[] servers = new Thread[numThreads];          // pipe server threads
        private Thread monitor;                                     // pipes' monitoring thread, restart themn when finished processing
        private int monitorSleepTime = 100;                         // sleep time (ms) for pipe threads' monitor
        private bool runMonitor = false;                            // are we monitoring and restarting waiting threads
        private Queue<string> messageQueue = new Queue<string>();   // messages to forward 
        private Thread forward;                                     // forwarding thread
        private bool runForward = false;                            // do we forwarding queue
        private int forwardSleepTime = 100;                         // sleep time (ms) for forwarding
        private StringBuilder errorMessage;                         // error messages
        const string registryRoot = "HKEY_LOCAL_MACHINE\\SOFTWARE\\Dzoka\\AxSync";
        private string connectionString = "";                       // connection string to database
        public event EventHandler CheckQueue;                       // used to test/debug in Form1
        private EventLog eventLog1;                                 // Windows Application log

        /// <summary>
        /// Constructor
        /// </summary>
        public Server()
        {
            int tempInt;
            eventLog1 = new EventLog("Application")
            {
                Source = "AXSync"
            };
            connectionString = (string)Registry.GetValue(registryRoot, "ConnectionString", "");
            if (int.TryParse((string)Registry.GetValue(registryRoot, "MonitorSleepTime", "100"), out tempInt))
            {
                monitorSleepTime = tempInt;
            }
            else
            {
                monitorSleepTime = 100;
            }
            if (int.TryParse((string)Registry.GetValue(registryRoot, "ForwardSleepTime", "100"), out tempInt))
            {
                forwardSleepTime = tempInt;
            }
            else
            {
                forwardSleepTime = 100;
            }
            if (int.TryParse((string)Registry.GetValue(registryRoot, "NumThreads", "3"), out tempInt))
            {
                numThreads = tempInt;
            }
            else
            {
                numThreads = 3;
            }
        }

        /// <summary>
        /// Log messages to Windows Application log
        /// </summary>
        /// <param name="message"></param>
        private void LogMessage(string message, EventLogEntryType type, int id)
        {
            if (eventLog1 != null)
            {
                try
                {
                    eventLog1.WriteEntry(message, type, id);
                }
                catch (Exception)
                {
                    return;             // could not write event log
                }
            }
        }

        /// <summary>
        /// Read last message from queue. Does not enqueue.
        /// </summary>
        /// <returns></returns>
        public string ReadQueue()
        {
            if (messageQueue.Count > 0)
            {
                //return messageQueue.Dequeue();
                return messageQueue.ElementAt(messageQueue.Count - 1);
            }
            else
            {
                return "";
            }
        }

        /// <summary>
        /// Starts all pipes and monitoring threads
        /// </summary>
        public void StartServer()
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                LogMessage("AXSync server could not start. Missing Connection string", EventLogEntryType.Error, 1);
                return;                             // do not start threads
            }
            for (int i = 0; i < numThreads; i++)
            {
                servers[i] = new Thread(waitConnection);
                servers[i].Name = "Pipe listener";
                servers[i].Start();
            }
            monitor = new Thread(monitorThreads);
            monitor.Name = "Pipe monitor";
            runMonitor = true;
            monitor.Start();

            forward = new Thread(forwardQueue);
            forward.Name = "Queue forwarder";
            runForward = true;
            forward.Start();

            LogMessage("AxSync server started", EventLogEntryType.Information, 0);
        }

        /// <summary>
        /// Stops monitoring thread, closes all running pipe threads
        /// </summary>
        public void StopServer()
        {
            runForward = false;
            if (forward != null)
            {
                forward.Join(forwardSleepTime * 2);
                forward.Abort();
            }

            runMonitor = false;
            if (monitor != null)
            {
                monitor.Join(monitorSleepTime * 2);                 // wait twice sleep time until thread ends
                monitor.Abort();
            }

            bool closing = true;
            while (closing)
            {
                closing = Client.Submit("Closing");                 // close waiting connections
            }
            LogMessage(String.Format("AxSync server stopped. Queue length = {0}", messageQueue.Count), EventLogEntryType.Information, 0);
        }

        /// <summary>
        /// Monitors pipes' threads and re-creates them
        /// </summary>
        private void monitorThreads()
        {
            while (runMonitor)
            {
                for (int i = 0; i < numThreads; i++)
                {
                    if (servers[i].IsAlive == false)
                    {
                        servers[i] = new Thread(waitConnection);
                        servers[i].Name = "Pipe server";
                        servers[i].Start();
                    }
                }
                Thread.Sleep(monitorSleepTime);
            }
        }

        /// <summary>
        /// Pipes thread
        /// </summary>
        private void waitConnection()
        {
            //var id = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            //var rule = new PipeAccessRule(id, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow);
            var rule = new PipeAccessRule("Everyone", PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow);
            PipeSecurity pipeSecurity = new PipeSecurity();
            pipeSecurity.SetAccessRule(rule);

            NamedPipeServerStream pipeServer = new NamedPipeServerStream("DzokaAxSync", PipeDirection.InOut, numThreads, PipeTransmissionMode.Message, PipeOptions.None, 4096, 4096, pipeSecurity);

            //NamedPipeServerStream pipeServer = new NamedPipeServerStream("AxSync", PipeDirection.In, numThreads);

            int threadId = Thread.CurrentThread.ManagedThreadId;
            pipeServer.WaitForConnection();
            string inp = "";
            try
            {
                StreamString ss = new StreamString(pipeServer);
                inp = ss.ReadString();
            }
            catch (IOException e)
            {
                inp = String.Format("Pipe server, IO exception: {0}", e.Message);
                LogMessage(inp, EventLogEntryType.Information, 1);
            }
            // if monitor is not running - we are closing waiting connections, do not save this into queue
            if (runMonitor)
            {
                messageQueue.Enqueue(inp);
            }
            pipeServer.Close();
        }

        /// <summary>
        /// Forwards queue to the destination
        /// </summary>
        private void forwardQueue()
        {
            int sqlResult;
            while (runForward)
            {
                if (messageQueue.Count > 0)
                {
                    errorMessage = new StringBuilder();
                    if (CheckQueue != null)
                    {
                        CheckQueue(this, new EventArgs());              // rise event, that queue is waiting
                    }

                    using (SqlConnection sqlConnection1 = new SqlConnection(connectionString))
                    {
                        using (SqlCommand sqlCommand1 = new SqlCommand())
                        {
                            sqlCommand1.Connection = sqlConnection1;
                            sqlCommand1.CommandType = CommandType.Text;

                            #region store the queue

                            do
                            {
                                sqlCommand1.CommandText = String.Format("INSERT INTO AxSync (message) VALUES ('{0}')", messageQueue.ElementAt(0));
                                if (sqlConnection1.State == ConnectionState.Closed)
                                {
                                    try
                                    {
                                        sqlConnection1.Open();
                                    }
                                    catch (Exception e)
                                    {
                                        errorMessage.AppendLine(e.Message);
                                        LogMessage(errorMessage.ToString(), EventLogEntryType.Information, 1);
                                        return;
                                    }
                                }
                                try
                                {
                                    sqlResult = sqlCommand1.ExecuteNonQuery();
                                }
                                catch (Exception e)
                                {
                                    errorMessage.AppendLine(e.Message);
                                    LogMessage(errorMessage.ToString(), EventLogEntryType.Information, 1);
                                    return;
                                }
                                if (sqlResult > 0)
                                {
                                    messageQueue.Dequeue();
                                }
                            }
                            while (messageQueue.Count > 0);

                            #endregion
                        }
                    }
                }
                Thread.Sleep(forwardSleepTime);
            }
        }
    }
}

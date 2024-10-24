// (c) 2023, 2024 Dzoka
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
    enum ErrorCodes : ushort
    {
        SqlConnectionOpen = 81,
        SqlExecute = 82,
        Starting = 83,
        Threading = 84
    }

    enum WarningCodes : ushort
    {
        WindowsRegistry = 41,
        Stopping = 42,
        QueueLength = 43
    }

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
        const string registryRoot = "HKEY_LOCAL_MACHINE\\SOFTWARE\\Dzoka\\AxSync";
        private string connectionString = "";                       // connection string to database
        public event EventHandler CheckQueue;                       // used to test/debug in Form1
        private EventLog eventLog1;                                 // Windows Application log

        /// <summary>
        /// Constructor
        /// </summary>
        public Server()
        {
            #region open Windows Application event log

            try
            {
                eventLog1 = new EventLog("Application", ".", "AXSync");
            }
            catch
            {
                eventLog1 = null;       // continue without evemt log
            }

            #endregion

            #region read settings from registry

            int tempInt;
            try
            {
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
            catch (Exception e)
            {
                LogMessage(String.Format("Read Windows registry exception {0}", e.Message), EventLogEntryType.Warning, (int)WarningCodes.WindowsRegistry);
            }

            #endregion
        }

        /// <summary>
        /// Log messages to Windows Application event log
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
        /// Used in Form1 for testing.
        /// </summary>
        /// <returns></returns>
        public string ReadQueue()
        {
            if (messageQueue.Count > 0)
            {
                return messageQueue.ElementAt(messageQueue.Count - 1);
            }
            else
            {
                return "";
            }
        }

        /// <summary>
        /// Starts working threads
        /// </summary>
        public bool StartServer()
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                LogMessage("AXSync server could not start. Missing Connection string", EventLogEntryType.Error, (int)ErrorCodes.Starting);
                return false;                   // do not start threads
            }
            monitor = new Thread(monitorThreads)
            {
                Name = "Pipe monitor",
            };
            runMonitor = true;
            try
            {
                monitor.Start();
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Monitor thread could not start, exception: {0}", e.Message), EventLogEntryType.Error, (int)ErrorCodes.Starting);
                return false;
            }
            forward = new Thread(forwardQueue)
            {
                Name = "Queue forwarder",
            };
            runForward = true;
            try
            {
                forward.Start();
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Forwarder thread could not start, exception: {0}", e.Message), EventLogEntryType.Error, (int)ErrorCodes.Starting);
                // TODO should we kill monitor thread before we return?
                return false;
            }
            return (monitor.IsAlive && forward.IsAlive);
        }

        /// <summary>
        /// Stops monitoring thread, closes all running pipe threads
        /// </summary>
        public void StopServer()
        {
            runMonitor = false;
            try
            {
                if (monitor != null)
                {
                    monitor.Join(monitorSleepTime * 2);         // wait twice sleep time until thread ends
                    monitor.Abort();
                }
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Monitor thread stop exception: {0}", e.Message), EventLogEntryType.Warning, (int)WarningCodes.Stopping);
            }
            runForward = false;
            try
            {
                if (forward != null)
                {
                    forward.Join(forwardSleepTime * 2);
                    forward.Abort();
                }
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Forwarder thread stop exception: {0}", e.Message), EventLogEntryType.Warning, (int)WarningCodes.Stopping);
            }
            bool closing = true;
            while (closing)
            {
                closing = Client.Submit("Closing");                 // close waiting connections
            }
            LogMessage(String.Format("AxSync server stopped. Queue length = {0}", messageQueue.Count), EventLogEntryType.Warning, (int)WarningCodes.QueueLength);
        }

        /// <summary>
        /// Monitors pipes' threads and re-creates them
        /// </summary>
        private void monitorThreads()
        {
            #region initialize listening pipes

            for (int i = 0; i < numThreads; i++)
            {
                servers[i] = new Thread(waitConnection)
                {
                    Name = "Pipe listener"
                };
                servers[i].Start();
            }

            #endregion

            #region monitor running pipes

            while (runMonitor)
            {
                for (int i = 0; i < numThreads; i++)
                {
                    if (servers[i].IsAlive == false)
                    {
                        try
                        {
                            servers[i] = new Thread(waitConnection)
                            {
                                Name = "Pipe listener"
                            };
                            servers[i].Start();
                        }
                        catch (Exception e)
                        {
                            LogMessage(String.Format("Pipe listener thread could not be started, exception: {0}", e.Message), EventLogEntryType.Error, (int)ErrorCodes.Starting);
                        }
                    }
                }
                Thread.Sleep(monitorSleepTime);
            }

            #endregion
        }

        /// <summary>
        /// Pipes thread
        /// </summary>
        private void waitConnection()
        {
            var rule = new PipeAccessRule("Everyone", PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow);
            PipeSecurity pipeSecurity = new PipeSecurity();
            pipeSecurity.SetAccessRule(rule);
            NamedPipeServerStream pipeServer = new NamedPipeServerStream("DzokaAxSync", PipeDirection.InOut, numThreads, PipeTransmissionMode.Message, PipeOptions.None, 4096, 4096, pipeSecurity);
            int threadId = Thread.CurrentThread.ManagedThreadId;
            pipeServer.WaitForConnection();
            string inp = "";
            try
            {
                StreamString ss = new StreamString(pipeServer);
                inp = ss.ReadString();
                if (runMonitor)
                {
                    // only if monitor is running, we queue the message, otherwise - we are closing threads, do not save this message into queue
                    messageQueue.Enqueue(inp);
                }
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Pipe server, exception: {0}", e.Message), EventLogEntryType.Error, (int)ErrorCodes.Threading);
            }
            finally
            {
                pipeServer.Close();
            }
        }

        /// <summary>
        /// Forwards queue to the destination
        /// </summary>
        private void forwardQueue()
        {
            StringBuilder errorMessage;
            int sqlResult;
            while (runForward)
            {
                if (messageQueue.Count > 0)
                {
                    errorMessage = new StringBuilder();
                    if (CheckQueue != null)
                    {
                        CheckQueue(this, new EventArgs());              // rise event, the queue is waiting
                    }

                    using (SqlConnection sqlConnection1 = new SqlConnection(connectionString))
                    {
                        using (SqlCommand sqlCommand1 = new SqlCommand())
                        {
                            sqlCommand1.Connection = sqlConnection1;
                            sqlCommand1.CommandType = CommandType.Text;
                            sqlCommand1.CommandText = "INSERT INTO AXSync (message) VALUES (@message)";

                            #region store the queue

                            do
                            {
                                sqlCommand1.Parameters.Clear();
                                sqlCommand1.Parameters.AddWithValue("@message", messageQueue.ElementAt(0));
                                if (sqlConnection1.State != ConnectionState.Open)
                                {
                                    try
                                    {
                                        sqlConnection1.Open();
                                    }
                                    catch (Exception e)
                                    {
                                        LogMessage(String.Format("Store the queue, connection open exception {0}", e.Message), EventLogEntryType.Error, (int)ErrorCodes.SqlConnectionOpen);
                                        break;                  // continue the thread
                                    }
                                }
                                try
                                {
                                    if (sqlConnection1.State == ConnectionState.Open)
                                    {
                                        sqlResult = sqlCommand1.ExecuteNonQuery();
                                    }
                                    else
                                    {
                                        sqlResult = 0;
                                        LogMessage(String.Format("Store the queue, connection could not be opened, connection state: {0}", 
                                            sqlConnection1.State.ToString()), EventLogEntryType.Error, (int)ErrorCodes.SqlConnectionOpen);
                                    }
                                }
                                catch (Exception e)
                                {
                                    errorMessage.AppendLine("Store the queue, execute command exception.");
                                    errorMessage.AppendLine(messageQueue.Dequeue());        // de-queue problemic message into application log
                                    errorMessage.AppendLine(e.Message);
                                    LogMessage(errorMessage.ToString(), EventLogEntryType.Error, (int)ErrorCodes.SqlExecute);
                                    break;                      // continue the thread
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

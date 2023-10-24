using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;

namespace Dzoka.AxSyncService
{
    public partial class AXSync : ServiceBase
    {
        AxSyncLibrary.Server pipeServer;

        public AXSync()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            pipeServer = new AxSyncLibrary.Server();
            pipeServer.StartServer();
        }

        protected override void OnStop()
        {
            pipeServer.StopServer();
        }
    }
}

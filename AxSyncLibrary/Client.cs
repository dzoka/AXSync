using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.IO.Pipes;

namespace Dzoka.AxSyncLibrary
{
    public static class Client
    {
        public static bool Submit(string msg)
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", "DzokaAxSync", PipeDirection.Out, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Impersonation))
            {
                try
                {
                    pipe.Connect(100);
                }
                catch (TimeoutException)
                {
                    return false;
                }
                StreamString ss = new StreamString(pipe);
                ss.WriteString(msg);
                return true;
            }
        }
    }
}

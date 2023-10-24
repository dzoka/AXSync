using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Dzoka.AxSyncForm
{
    public partial class Form1 : Form
    {
        AxSyncLibrary.Server srv;

        public Form1()
        {
            InitializeComponent();
        }

        private void buttonSubmit_Click(object sender, EventArgs e)
        {
            AxSyncLibrary.Client.Submit(textBoxMessage.Text);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            srv = new AxSyncLibrary.Server();
            srv.StartServer();
            srv.CheckQueue += new EventHandler(showQueue);
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            srv.StopServer();
        }

        private void showQueue(object sender, EventArgs e)
        {
            listBox1.Invoke(new loadListBox(loadList));
        }

        private delegate void loadListBox();

        private void loadList()
        {
            listBox1.Items.Add(srv.ReadQueue());
        }
    }
}

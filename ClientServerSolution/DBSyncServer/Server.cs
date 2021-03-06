﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Net;
using FileParser;
using DBOps;
using Newtonsoft.Json;

namespace DBSyncServer
{
	public partial class Server : Form
	{
		private TcpListener listener;
		private Thread tcpThread;

		private MsSqlOps dbConn;

		private IniFile ini;
		//private Dictionary<string, int> lastIDs;

		private bool dbConnected = false;
		private bool tcpOpened = false;

		public Server()
		{
			InitializeComponent();
		}

		// 给tcp线程操作日志的委托
		private delegate void logDelegate(string logStr);
		private void log(string logStr)
		{
			if (this.logInfoBox.InvokeRequired)
			{
				logDelegate logD = new logDelegate(log);
				this.BeginInvoke(logD, new object[] { logStr });
				return;
			}
			this.logInfoBox.AppendText(string.Format(
				"[{0}] {1}", DateTime.Now.ToLongTimeString(), logStr));
			this.logInfoBox.AppendText(Environment.NewLine);
			this.logInfoBox.ScrollToCaret();
		}

		private string getConnStr()
		{
			// 参数详见 https://msdn.microsoft.com/en-us/library/system.data.sqlclient.sqlconnection.connectionstring%28v=vs.110%29.aspx

			SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();

			if (this.modeWin.Checked)
			{
				var server = this.dbServerName.Text;
				builder.Add("Data Source", server);
				builder.Add("Integrated Security", "SSPI");
			}
			else
			{
				builder.Add("Data Source", this.dbIP.Text + "," + this.dbPort.Value.ToString());
				builder.Add("User ID", this.userName.Text);
				builder.Add("Password", this.password.Text);
				builder.Add("Network Library", "DBMSSOCN");
			}

			builder.Add("Initial Catalog", this.dbName.Text);

			return builder.ConnectionString;
		}

		private void clientCallback(TcpClient newClient)
		{
			TcpClient client = newClient;
			NetworkStream stream2Client = client.GetStream();
			StreamReader reader = new StreamReader(stream2Client);
			StreamWriter writer = new StreamWriter(stream2Client);
			writer.AutoFlush = true;

			while (true)
			{
				string recvStr = reader.ReadLine();
				if (recvStr == "88")
				{
					this.log("Client closed the connection!");
					break;
				}
				if (!string.IsNullOrEmpty(recvStr))
				{
					this.log(string.Format("Received {0} byte(s) data.", recvStr.Length));
					DataSet dataSet = JsonConvert.DeserializeObject<DataSet>(recvStr);
					this.dbConn.updateDataTable(dataSet.Tables[0]);

					var rowNum = dataSet.Tables[0].Rows.Count;
					var maxID = int.Parse(dataSet.Tables[0].Rows[rowNum - 1]["SysId"].ToString());

					this.log("Now sending ACK to client...");
					writer.WriteLine(dataSet.Tables[0].TableName + "," + maxID);
					// 释放资源
					dataSet.Dispose();
				}
			}
			client.Close();
		}

		// runServer作为后台线程，防止UI线程阻塞
		private void runServer()
		{
			while (true)
			{
				this.log("Waiting for a connection...");
				TcpClient newClient = this.listener.AcceptTcpClient();
				this.log("Accept a new client");

				// 一对一，不需要多线程方式
				this.clientCallback(newClient);

				// 多线程方式
				//Thread clientThread = new Thread(this.clientCallback);
				//clientThread.Start(newClient);
			}
		}

		private void myNotify_DoubleClick(object sender, EventArgs e)
		{
			if (this.ShowInTaskbar == false)
			{
				this.ShowInTaskbar = true;
				this.myNotify.Visible = false;
				this.Show();
				this.Activate();
				this.WindowState = FormWindowState.Normal;
			}
		}

		private void Server_Resize(object sender, EventArgs e)
		{
			if (this.WindowState == FormWindowState.Minimized)
			{
				this.WindowState = FormWindowState.Minimized;
				this.ShowInTaskbar = false;
				this.Hide();
				this.myNotify.Visible = true;
				this.myNotify.ShowBalloonTip(1000);
			}
		}

		private void showWinToolStripMenuItem_Click(object sender, EventArgs e)
		{
			myNotify_DoubleClick(sender, e);
		}

		private void exitToolStripMenuItem_Click(object sender, EventArgs e)
		{
			btnExit_Click(sender, e);
		}

		private void btnSaveConfig_Click(object sender, EventArgs e)
		{
			var mode = (this.modeWin.Checked) ? 0 : 1;
			ini.WriteInteger("DBConnection", "Mode", mode);
			ini.WriteString("DBConnection", "Server", this.dbServerName.Text);
			ini.WriteString("DBConnection", "IP", this.dbIP.Text);
			ini.WriteInteger("DBConnection", "Port", (int)this.dbPort.Value);
			ini.WriteString("DBConnection", "UID", this.userName.Text);
			ini.WriteString("DBConnection", "PW", this.password.Text);

			ini.WriteString("DBConnection", "DB", this.dbName.Text);

			ini.WriteString("TCPServer", "IP", this.tcpServerIP.Text);
			ini.WriteInteger("TCPServer", "Port", (int)this.tcpServerPort.Value);

			this.log("Configure write done!");
		}

		private void btnStart_Click(object sender, EventArgs e)
		{
			try
			{
				if (this.dbConnected == false)
				{
					// init mssql connection
					this.dbConn = new MsSqlOps(getConnStr());
					this.dbConnected = true;
					this.log("Connect to database succeed!");
				}

				// init tcp listener
				var lIP = this.tcpServerIP.Text;
				var lPort = (int)this.tcpServerPort.Value;
				this.listener = new TcpListener(IPAddress.Parse(lIP), lPort);

				this.listener.Start();
				this.tcpOpened = true;
				this.btnStart.Enabled = false;
				this.btnExit.Enabled = true;
				this.log("Server started!");

				// 新线程开启
				this.tcpThread = new Thread(this.runServer);
				this.tcpThread.IsBackground = true;
				this.tcpThread.Start();
			}
			catch (SqlException ex)
			{
				this.log("Link to database failed!");
				this.log(ex.Message);
			}
			catch (SocketException ex)
			{
				this.log("Open tcp listener failed!");
				this.log(ex.Message);
			}
		}

		// 停止tcp线程，关闭数据库和tcp listener，
		private void btnExit_Click(object sender, EventArgs e)
		{
			if (this.tcpOpened)
			{
				if (this.dbConnected)
				{
					this.dbConn.close();
					this.dbConnected = false;
				}

				if (this.tcpThread.ThreadState != ThreadState.Unstarted)
				{
					this.tcpThread.Abort();
					this.log("TCP server thread closed!");
				}
				this.listener.Stop();
				this.tcpOpened = false;

				this.log("Link closed!");
				this.log("Now you can link to database and tcp server again.");
			}

			this.btnStart.Enabled = true;
			this.btnExit.Enabled = false;
		}

		private void modeSql_CheckedChanged(object sender, EventArgs e)
		{
			if (this.modeSql.Checked)
			{
				this.dbGroupBox.Enabled = true;
				this.dbServerName.Enabled = false;
			}
			else
			{
				this.dbGroupBox.Enabled = false;
				this.dbServerName.Enabled = true;
			}
		}

		private void Server_Load(object sender, EventArgs e)
		{
			FileInfo fInfo = new FileInfo("Config.ini");
			if (!fInfo.Exists)
			{
				this.log("Missing configure file!");
				this.log("Now load default configures.");
				this.log("Please press save configuration button after you filled other blanks.");
			}
			this.ini = new IniFile("Config.ini");
			var mode = this.ini.ReadInteger("DBConnection", "Mode", 1);
			if (0 == mode)
			{
				this.dbServerName.Text = ini.ReadString("DBConnection", "Server", ".");
				this.dbGroupBox.Enabled = false;
				this.modeSql.Checked = false;
			}
			else
			{
				this.modeWin.Checked = false;
				this.dbServerName.Enabled = false;
				this.dbIP.Text = this.ini.ReadString("DBConnection", "IP", "127.0.0.1");
				this.dbPort.Value = this.ini.ReadInteger("DBConnection", "Port", 1433);
				this.userName.Text = this.ini.ReadString("DBConnection", "UID", "Administrator");
				this.password.Text = this.ini.ReadString("DBConnection", "PW", "");
			}

			this.dbName.Text = this.ini.ReadString("DBConnection", "DB", "");

			this.tcpServerIP.Text = this.ini.ReadString("TCPServer", "IP", "0.0.0.0");
			this.tcpServerPort.Value = this.ini.ReadInteger("TCPServer", "Port", 54321);

			this.log("Load configuration done!");

			// init last syncID
			//this.lastIDs = new Dictionary<string, int>();
			//foreach (string tableName in Tables.TableNames)
			//{
			//	this.lastIDs[tableName] = this.ini.ReadInteger("LastID", tableName, 0);
			//}
			this.log("Load last syncIDs done!");
		}

		private void Server_FormClosing(object sender, FormClosingEventArgs e)
		{
			if (this.tcpOpened)
			{
				if (MessageBox.Show(
					"服务器仍在监听，确认要关闭吗？",
					"确认关闭", MessageBoxButtons.OKCancel) == DialogResult.Cancel)
				{
					e.Cancel = true;
					return;
				}
			}
			btnExit_Click(sender, e);
		}
	}
}

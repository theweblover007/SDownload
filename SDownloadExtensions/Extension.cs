﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SDownloadExtensions
{
    public partial class ExtensionForm : Form
    {
        private const String ChromeKey = "Google Chrome";
        private const String ChromeUrl =
            "https://chrome.google.com/webstore/detail/sdownload/dkflmdcolphnomonabinogaegbjbnbbm";

        public ExtensionForm()
        {
            InitializeComponent();

            RegistryKey InstalledBrowsers =
                Registry.LocalMachine.OpenSubKey("SOFTWARE").OpenSubKey("Clients").OpenSubKey("StartMenuInternet");

            if (InstalledBrowsers == null)
                return;

            if (InstalledBrowsers.OpenSubKey(ChromeKey) == null)
            {
                chromeInstallButton.Enabled = false;
                chromeInstallButton.Text = "Chrome not Installed!";
            }

            chromeInstallButton.Click += (sender, args) =>
                                             {
                                                 String chromeEXE = "chrome";
                                                 Process.Start(chromeEXE, ChromeUrl);
                                             };
        }
    }
}
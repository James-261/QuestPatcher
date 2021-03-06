﻿using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.ComponentModel;
using Serilog.Core;

namespace QuestPatcher
{
    public class DebugBridge
    {
        private static readonly CompareInfo compareInfo = new CultureInfo((int) CultureTypes.AllCultures).CompareInfo;

        public string APP_ID { get; } = File.ReadAllText(AppDomain.CurrentDomain.BaseDirectory + "/appId.txt");

        private MainWindow window;
        private Logger logger;
        private bool adbOnPath = false;

        public DebugBridge(MainWindow window)
        {
            this.window = window;
            this.logger = window.Logger;
        }

        private string handlePlaceholders(string command)
        {
            command = command.Replace("{app-id}", APP_ID);
            return command;
        }

        public string runCommand(string command)
        {
            return runCommandAsync(command).Result;
        }

        private bool containsIgnoreCase(string str, string lookingFor)
        {
            return compareInfo.IndexOf(str, lookingFor, CompareOptions.IgnoreCase) >= 0;
        }

        public async Task<string> runCommandAsync(string command)
        {
            Process process = new Process();
            process.StartInfo.FileName = (adbOnPath ? "" : window.DATA_PATH + "platform-tools/") + (OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            process.StartInfo.Arguments = handlePlaceholders(command);
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            logger.Verbose("Executing ADB command: adb " + process.StartInfo.Arguments);

            process.Start();

            string errorOutput = process.StandardError.ReadToEnd();
            string output = process.StandardOutput.ReadToEnd();

            logger.Verbose("Standard output: " + output);
            logger.Verbose("Error output: " + errorOutput);

            await process.WaitForExitAsync();

            if (containsIgnoreCase(output, "error") || containsIgnoreCase(output, "failed"))
            {
                throw new Exception(output);
            }
            string fullOutput = errorOutput + output;

            return fullOutput;
        }

        private async Task checkIfAdbOnPath()
        {
            adbOnPath = true;

            try
            {
                await runCommandAsync("version");
            }   catch (Exception) // Thrown if the file doesn't exist
            {
                adbOnPath = false;
            }
        }

        // Downloads the Android platform-tools if they aren't present
        public async Task InstallIfMissing()
        {
            await checkIfAdbOnPath();
            if(adbOnPath)
            {
                logger.Information("Located ADB installation on PATH");
                return;
            }

            if(Directory.Exists(window.DATA_PATH + "platform-tools/"))
            {
                logger.Information("Platform-tools already installed");
                return;
            }

            WebClient webClient = new WebClient();

            logger.Information("platform-tools missing, installing!");
            await webClient.DownloadFileTaskAsync(findPlatformToolsLink(), Path.GetTempPath() + "platform-tools.zip");
            logger.Information("Extracting . . .");
            await Task.Run(() => {
                ZipFile.ExtractToDirectory(Path.GetTempPath() + "platform-tools.zip", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/QuestPatcher");
            });
            File.Delete(Path.GetTempPath() + "platform-tools.zip");

            logger.Information("Done!");
        }

        public string findPlatformToolsLink()
        {
            if (OperatingSystem.IsWindows())
            {
                return "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
            }
            else if (OperatingSystem.IsLinux())
            {
                return "https://dl.google.com/android/repository/platform-tools-latest-linux.zip";
            }
            else if (OperatingSystem.IsMacOS())
            {
                return "https://dl.google.com/android/repository/platform-tools-latest-darwin.zip";
            }

            throw new Exception("ADB is not available for your operating system!");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO.Compression;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Interactivity;
using System.Net;
using Serilog.Core;

namespace QuestPatcher {
    public class ModsManager
    {
        // Temporarily extract the ZIP file here so that we can use ADB push
        private string INSTALLED_MODS_PATH = "sdcard/QuestPatcher/{app-id}/installedMods/";
        private string EXTRACTED_MODS_PATH;
        private string DEPENDENCY_PATH;

        private MainWindow window;
        private Logger logger;
        private DebugBridge debugBridge;

        // Map of Mod ID -> Mod Manifest
        public Dictionary<string, ModManifest> InstalledMods { get; } = new Dictionary<string, ModManifest>();
        private Random random = new Random();

        public ModsManager(MainWindow window) {
            this.window = window;
            this.debugBridge = window.DebugBridge;
            this.logger = window.Logger;
            this.EXTRACTED_MODS_PATH = window.TEMP_PATH + "extractedMods/";
            this.DEPENDENCY_PATH = window.TEMP_PATH + "downloadedDepdendencies/";

            Directory.CreateDirectory(DEPENDENCY_PATH);
            Directory.CreateDirectory(EXTRACTED_MODS_PATH);
        }

        public async Task LoadModsFromQuest() {
            string modsNonSplit;
            try
            {
                await debugBridge.runCommandAsync("shell mkdir -p \"" + INSTALLED_MODS_PATH + "\"");
                await debugBridge.runCommandAsync("shell mkdir -p \"sdcard/Android/data/{app-id}/files/mods\"");
                await debugBridge.runCommandAsync("shell mkdir -p \"sdcard/Android/data/{app-id}/files/libs\"");

                // List the manifests in the installed mods directory for this app
                modsNonSplit = await debugBridge.runCommandAsync("shell ls -R \"" + INSTALLED_MODS_PATH + "\"");
            }
            catch (Exception ex)
            {
                logger.Fatal("An error occurred while loading mods from the Quest: " + ex.Message);
                logger.Verbose(ex.ToString());
                return;
            }

            // Remove unnecessary things that ADB adds
            string[] rawPaths = modsNonSplit.Split("\n");
            List<string> parsedPaths = new List<string>();
            for(int i = 0; i < rawPaths.Length; i++) {
                if(i == 0 || i == rawPaths.Length - 1) {continue;}

                parsedPaths.Add(INSTALLED_MODS_PATH + rawPaths[i].Replace("\r", ""));
            }

            foreach(string path in parsedPaths) {
                string contents = await debugBridge.runCommandAsync("shell cat \"" + path + "\"");

                try
                {
                    ModManifest manifest = await ModManifest.Load(contents);
                    addManifest(manifest);
                }
                catch (Exception ex)
                {
                    logger.Error("An error occured while loading " + Path.GetFileNameWithoutExtension(path) + " from the Quest: " + ex.Message);
                    logger.Verbose(ex.ToString());
                }
            }
        }

        public async Task InstallDependency(DependencyInfo dependency)
        {
            ModManifest? existing = null;
            InstalledMods.TryGetValue(dependency.Id, out existing);

            if(existing == null)
            {
                if(dependency.DownloadIfMissing == null)
                {
                    throw new Exception("Dependency " + dependency.Id + " is not installed, and does not specify a download path if missing");
                }

                WebClient webClient = new WebClient();

                logger.Information("Downloading dependency " + dependency.Id);

                string downloadedPath = DEPENDENCY_PATH + dependency.Id + ".qmod";
                await webClient.DownloadFileTaskAsync(dependency.DownloadIfMissing, downloadedPath);
                await InstallMod(downloadedPath);

                File.Delete(downloadedPath); // Remove the temporarily downloaded mod

                ModManifest dependencyManifest = InstalledMods[dependency.Id];
                
                // Sanity checks that the download link actually pointed to the right mod
                if(!dependency.ParsedVersion.IsSatisfied(dependencyManifest.ParsedVersion))
                {
                    await UninstallMod(dependencyManifest);
                    throw new Exception("Downloaded dependency " + dependency.Id + " was not within the version stated in the mod's manifest");
                }

                if(dependency.Id != dependencyManifest.Id)
                {
                    await UninstallMod(dependencyManifest);
                    throw new Exception("Downloaded dependency had ID " + dependencyManifest.Id + ", whereas the dependency stated ID " + dependency.Id);
                }
            }   else    {
                if(!dependency.ParsedVersion.IsSatisfied(existing.ParsedVersion))
                {
                    throw new Exception("Dependency " + dependency.Id + " is installed, but the installed version is not within the given version range");
                }
            }
        }

        public async Task InstallMod(string path) {
            string extractPath = EXTRACTED_MODS_PATH + Path.GetFileNameWithoutExtension(path) + "/";
            Directory.CreateDirectory(extractPath);

            try
            {
                logger.Information("Extracting mod . . .");
                Directory.CreateDirectory(extractPath);
                ZipFile.ExtractToDirectory(path, extractPath);

                // Read the manifest
                logger.Information("Loading manifest . . .");
                string manifestText = await File.ReadAllTextAsync(extractPath + "mod.json");
                ModManifest manifest = await ModManifest.Load(manifestText);

                if (manifest.PackageId != debugBridge.APP_ID)
                {
                    throw new Exception("This mod is not intended for the selected game!");
                }

                if (InstalledMods.ContainsKey(manifest.Id))
                {
                    throw new Exception("Attempted to install a mod when it was already installed");
                }

                foreach(DependencyInfo dependency in manifest.Dependencies)
                {
                    await InstallDependency(dependency);
                }

                // Copy all of the SO files
                foreach (string libraryPath in manifest.LibraryFiles)
                {
                    logger.Information("Copying mod file " + libraryPath);
                    await debugBridge.runCommandAsync("push \"" + extractPath + libraryPath + "\" \"sdcard/Android/data/{app-id}/files/libs/" + libraryPath + "\"");
                }

                foreach (string modFilePath in manifest.ModFiles)
                {
                    logger.Information("Copying library file " + modFilePath);
                    await debugBridge.runCommandAsync("push \"" + extractPath + modFilePath + "\" sdcard/Android/data/{app-id}/files/mods/" + modFilePath);
                }

                foreach (FileCopyInfo fileCopy in manifest.FileCopies)
                {
                    logger.Information("Copying file " + fileCopy.Name + " to " + fileCopy.Destination);
                    await debugBridge.runCommandAsync("push " + extractPath + fileCopy.Name + " " + fileCopy.Destination);
                }

                // Store that the mod was successfully installed
                logger.Information("Copying manifest . . .");
                debugBridge.runCommand("push \"" + extractPath + "mod.json\" \"" + INSTALLED_MODS_PATH + manifest.Id + ".json\"");

                addManifest(manifest);
                logger.Information("Done!");
            }
            finally
            {

                Directory.Delete(extractPath, true);
            }
        }

        public async Task UninstallMod(ModManifest manifest)
        {
            logger.Information("Uninstalling mod with ID " + manifest.Id + " . . .");
            window.InstalledModsPanel.Children.Remove(manifest.GuiElement);
            InstalledMods.Remove(manifest.Id);

            foreach (string modFilePath in manifest.ModFiles) {
                logger.Information("Removing mod file " + modFilePath);
                await debugBridge.runCommandAsync("shell rm -f \"sdcard/Android/data/{app-id}/files/mods/" + modFilePath + "\""); // Remove each mod file
            }
            
            foreach (string libraryPath in manifest.LibraryFiles)
            {
                // Only remove libraries if they aren't used by another mod
                bool isUsedElsewhere = false;
                foreach(ModManifest otherManifest in InstalledMods.Values)
                {
                    if(otherManifest.LibraryFiles.Contains(libraryPath))
                    {
                        logger.Information("Other mod " + otherManifest.Id + " still needs library " + libraryPath + ", not removing");
                        isUsedElsewhere = true;
                        break;
                    }
                }

                if(!isUsedElsewhere)
                {
                    logger.Information("Removing library file " + libraryPath);
                    await debugBridge.runCommandAsync("shell rm -f \"sdcard/Android/data/{app-id}/files/libs/" + libraryPath + "\"");
                }
            }

            foreach (FileCopyInfo fileCopy in manifest.FileCopies)
            {
                logger.Information("Removing copied file " + fileCopy.Destination);
                await debugBridge.runCommandAsync("shell rm -f \"" + fileCopy.Destination + "\"");
            }

            logger.Information("Removing mod manifest . . .");
            await debugBridge.runCommandAsync("shell rm -f \"" + INSTALLED_MODS_PATH + manifest.Id + ".json\""); // Remove the mod manifest

            if (!manifest.IsLibrary)
            {
                await removeUnusedLibraries();
            }
            logger.Information("Done!");
        }

        // Recursively removed any unused libraries.
        // Not very efficient but gets the job done
        private async Task removeUnusedLibraries()
        {
            logger.Information("Cleaning unused libraries . . .");

            int lastSize = -1;
            while (InstalledMods.Count != lastSize) // Keep attempting to remove libraries until none get removed this time
            {
                lastSize = InstalledMods.Count;

                List<ModManifest> unused = new List<ModManifest>();
                foreach (ModManifest manifest in InstalledMods.Values)
                {
                    if (!manifest.IsLibrary) { continue; } // Mods aren't uninstalled if unused

                    // Check if any other mods/libraries depend on this one
                    bool used = false;
                    foreach (ModManifest otherManifest in InstalledMods.Values)
                    {

                        if (otherManifest.DependsOn(manifest.Id))
                        {
                            used = true;
                            break;
                        }
                    }

                    if (!used)
                    {
                        unused.Add(manifest);
                    }
                }

                // Uninstall any unused libraries this iteration
                foreach (ModManifest manifest in unused)
                {
                    await UninstallMod(manifest);
                }
            }
        }

        // Kind of janky, but there isn't another good way to do this unless I set up MVVM which will take ages
        private void addManifest(ModManifest manifest) {
            Border border = new Border();
            border.Padding = new Thickness(5);
            border.BorderThickness = new Thickness(1);
            border.BorderBrush = Brushes.Gray;
            border.CornerRadius = new CornerRadius(3);

            StackPanel stackPanel = new StackPanel();
            border.Child = stackPanel;

            stackPanel.Orientation = Avalonia.Layout.Orientation.Vertical;
            
            TextBlock name = new TextBlock();
            name.Text = "Mod Name: " + manifest.Name;

            TextBlock author = new TextBlock();
            author.Text = "Mod Author: " + manifest.Author;
            
            TextBlock version = new TextBlock();
            version.Text = "Mod Version: " + manifest.Version;

            TextBlock gameVersion = new TextBlock();
            gameVersion.Text = "Intended for game version: " + manifest.PackageVersion;

            TextBlock a = new TextBlock();
            a.Text = "william gay";
            a.FontSize = 6;

            Button uninstall = new Button();
            if(manifest.IsLibrary)
            {
                // Libraries should not be uninstalled manually.
                // Instead, they are automatically uninstalled whenever there aren't any mods that use them installed.
                uninstall.Foreground = Brushes.Red;
                uninstall.Content = "Force Uninstall Library";
            }
            else
            {
                uninstall.Content = "Uninstall Mod";
            }

            stackPanel.Children.Add(name);
            stackPanel.Children.Add(version);
            stackPanel.Children.Add(gameVersion);
            stackPanel.Children.Add(author);
            stackPanel.Children.Add(uninstall);
            if(random.Next() % 10 == 0)
            {
                stackPanel.Children.Add(a);
            }

            manifest.GuiElement = border;

            window.InstalledModsPanel.Children.Add(border);
            InstalledMods[manifest.Id] = manifest;

            uninstall.Click += async delegate (object? sender, RoutedEventArgs args)
            {
                try
                {
                    if(manifest.IsLibrary)
                    {
                        logger.Warning("WARNING: Libraries should not be uninstalled manually. They are automatically uninstalled when no mods that use them are installed");
                    }

                    // Uninstall the mod from the Quest
                    await UninstallMod(manifest);

                    window.ModInstallErrorText.IsVisible = false;
                }
                catch (Exception ex)
                {
                    window.ModInstallErrorText.IsVisible = true;
                    window.ModInstallErrorText.Text = "Error while uninstalling mod: " + ex.Message;
                }
            };

        }
    }
}
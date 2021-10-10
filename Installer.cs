﻿using MelonLauncher.Forms;
using MelonLoader.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MelonLauncher;
using System.Net;
using MelonLauncher.Managers;
using System.Security.Cryptography;
using System.IO.Compression;

namespace MelonLauncher
{
    public class Installer
    {
        // string is the file/folder name and bool is folder if true
        public static readonly Tuple<string, bool>[] mlRootPaths =
        {
            new Tuple<string, bool>("MelonLoader", true),
            new Tuple<string, bool>("version.dll", false),
            new Tuple<string, bool>("winhttp.dll", false),
            new Tuple<string, bool>("winmm.dll", false),
            new Tuple<string, bool>("NOTICE.txt", false),
            new Tuple<string, bool>("README.MD", false),
            new Tuple<string, bool>("CHANGELOG.MD", false),
            new Tuple<string, bool>("LICENSE", false),
        };

        public static bool CheckForML(LibraryGame.Info game)
        {
            foreach (var a in mlRootPaths)
            {
                var path = Path.Combine(game.dir, a.Item1);
                if (a.Item2 ? Directory.Exists(path) : File.Exists(path))
                    return true;
            }
            return false;
        }

        /// <returns>The download task, the verify task and the extract task.</returns>
        public static Tuple<Task, Task, Task> Install(string version, LibraryGame.Info game, bool addToLib)
        {
            MelonLauncherForm.instance.GetLibGameByPath(game.path)?.UpdatingStarted();

            var inst = new Installer(version, game);
            var down = new Task((curTask) => inst.InstallDownloadTask(curTask), $"Download MelonLoader {version}");
            var verify = new Task((curTask) => inst.InstallVerifyTask(curTask), $"Verify Downloaded Package", down);
            var extract = new Task((curTask) => inst.InstallExtractTask(curTask), $"Extract MelonLoader {version} to {game.name}", verify);

            extract.onFinishedCallback += () =>
            {
                var gm = MelonLauncherForm.instance.GetLibGameByPath(game.path);
                if (gm == null && !extract.Failed)
                {
                    if (addToLib)
                        MelonLauncherForm.instance.CreateLibraryGame(game, false);
                }
                else
                {
                    gm.UpdatingFinished();
                }
            };

            MelonLauncherForm.instance.AddTask(down);
            MelonLauncherForm.instance.AddTask(verify);
            MelonLauncherForm.instance.AddTask(extract);
            return new Tuple<Task, Task, Task>(down, verify, extract);
        }

        public static Task Uninstall(LibraryGame.Info game)
        {
            return (Task)MelonLauncherForm.instance.Invoke(new Func<Task>(() =>
            {
                var task = new Task((t) => UninstallTask(game, t), "Uninstall MelonLoader from " + game.name);
                MelonLauncherForm.instance.AddTask(task);
                return task;
            }));
        }

        private static void UninstallTask(LibraryGame.Info game, Task task)
        {
            var paths = new List<Tuple<string, bool>>();
            foreach (var a in mlRootPaths)
            {
                var path = Path.Combine(game.dir, a.Item1);
                if (a.Item2 ? Directory.Exists(path) : File.Exists(path))
                    paths.Add(a);
            }

            for (int a = 0; a < paths.Count; a++)
            {
                var elm = paths[a];
                var path = Path.Combine(game.dir, elm.Item1);
                try
                {
                    if (elm.Item2)
                        Directory.Delete(path, true);
                    else
                        File.Delete(path);
                }
                catch { }

                task.ProgressBarPercentage = (a + 1) / paths.Count * 100;
            }

            task.FinishTask();
        }

        public bool legacy;
        public string version;
        public LibraryGame.Info game;
        public string mlTempPath;

        private Installer(string version, LibraryGame.Info game)
        {
            this.version = version;
            this.game = game;
        }

        private void InstallExtractTask(Task task)
        {
            if (CheckForML(game))
            {
                task.WaitForTask(Uninstall(game));
            }

            var str = File.Open(mlTempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var archive = new ZipArchive(str);
            var entries = archive.Entries.ToArray();
            long finalSize = 0;
            foreach (var ent in entries)
                finalSize += ent.Length;
            var len = entries.Length;
            long finishedBytes = 0;
            for (int a = 0; a < len; a++)
            {
                var file = entries[a];
                string completeFileName = Path.Combine(game.dir, file.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(completeFileName));
                if (string.IsNullOrEmpty(file.Name))
                    continue;
                file.ExtractToFile(completeFileName, true);
                finishedBytes += file.Length;
                task.ProgressBarPercentage = (int)(finishedBytes / (double)finalSize * 100d);
            }
            archive.Dispose();
            str.Dispose();

            task.ProgressBarPercentage = 100;

            task.FinishTask();
        }

        private void InstallVerifyTask(Task task)
        {
            string address = string.Concat(new string[]
            {
                Constants.melonLoaderReleases,
                "/download/",
                version,
                "/MelonLoader.",
                (!legacy && game.x86) ? "x86" : "x64",
                ".sha512"
            });

            var wc = new WebClient();

            wc.DownloadProgressChanged += (sender, e) => task.ProgressBarPercentage = (int)(e.ProgressPercentage * 0.9f);
            wc.DownloadStringCompleted += (sender, e) =>
            {
                wc.Dispose();

                if (e.Error != null)
                {
                    task.FailTask("Failed to download the SHA512 Hash of MelonLoader " + version + ".", e.Error);
                    return;
                }
                else if (e.Cancelled)
                {
                    task.FailTask("The download of the SHA512 Hash of MelonLoader " + version + "has been cancelled.");
                    return;
                }

                var hash = e.Result;
                byte[] array = new SHA512Managed().ComputeHash(File.ReadAllBytes(mlTempPath));
                task.ProgressBarPercentage = 100;
                if (array == null || array.Length == 0)
                {
                    task.FailTask("Failed to compute a SHA512 Hash of the downloaded package.");
                    return;
                }

                string text = BitConverter.ToString(array).Replace("-", string.Empty);
                if (string.IsNullOrEmpty(text))
                {
                    task.FailTask("Failed to convert the computed SHA512 hash to string.");
                    return;
                }

                if (!text.Equals(hash))
                {
                    task.FailTask("The downloaded package is either corrupted or has been edited, the SHA512 Hash doesn't match the original one!");
                    return;
                }

                task.FinishTask();
            };

            wc.DownloadStringAsync(new Uri(address));
        }

        private string InstallDownloadTask(Task task)
        {
            if (string.IsNullOrEmpty(version))
            {
                task.FailTask("Version is null or empty.");
                return null;
            }

            if (version[0] != 'v')
                version = 'v' + version;

            if (!Program.releasesAPI.ReleasesTbl.Any(x => x.Version == version))
            {
                task.FailTask("Invalid version selected.");
                return null;
            }

            legacy = version.StartsWith("v0.1") || version.StartsWith("v0.2");


            string uriString = string.Concat(new string[]
            {
                Constants.melonLoaderReleases,
                "/download/",
                version,
                "/MelonLoader.",
                (!legacy && game.x86) ? "x86" : "x64",
                ".zip"
            });
            Console.WriteLine(uriString);

            mlTempPath = TempPath.CreateTempFile();

            var wc = new WebClient();
            wc.DownloadProgressChanged += (sender, e) => task.ProgressBarPercentage = e.ProgressPercentage;
            wc.DownloadFileCompleted += (sender, e) =>
            {
                wc.Dispose();
                if (e.Error != null)
                    task.FailTask("Failed to download MelonLoader " + version + ".", e.Error);
                else if (e.Cancelled)
                    task.FailTask("The download of MelonLoader " + version + "has been cancelled.");
                else
                    task.FinishTask();
            };

            try
            {
                wc.DownloadFileAsync(new Uri(uriString), mlTempPath);
            }
            catch (Exception ex)
            {
                task.FailTask("Failed to download MelonLoader " + version + ".", ex);
                wc.Dispose();
                return null;
            }
            return mlTempPath;
        }
    }
}

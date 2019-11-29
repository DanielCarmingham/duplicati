using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duplicati.Library.Interface;
using Duplicati.Library.Main;
using Duplicati.Library.Main.Database;
using NUnit.Framework;
using Utility = Duplicati.Library.Utility.Utility;

namespace Duplicati.UnitTest
{
    public class DisruptionTests : BasicSetupHelper
    {
        // Files to create in MB.
        private readonly int[] fileSizes = {10, 20, 30};

        private void ModifySourceFiles()
        {
            foreach (int size in this.fileSizes)
            {
                byte[] data = new byte[size * 1024 * 1024];
                Random rng = new Random();
                rng.NextBytes(data);
                File.WriteAllBytes(Path.Combine(this.DATAFOLDER, size + "MB"), data);
            }
        }

        private async Task RunPartialBackup(Controller controller)
        {
            this.ModifySourceFiles();

            // ReSharper disable once AccessToDisposedClosure
            Task backupTask = Task.Run(() => controller.Backup(new[] {this.DATAFOLDER}));

            // Block for a small amount of time to allow the ITaskControl to be associated
            // with the Controller.  Otherwise, the call to Stop will simply be a no-op.
            Thread.Sleep(1000);

            controller.Stop(true);
            await backupTask.ConfigureAwait(false);
        }

        public override void SetUp()
        {
            base.SetUp();
            this.ModifySourceFiles();
        }

        [Test]
        [Category("Disruption")]
        public async Task KeepTimeRetention()
        {
            // Choose a dblock size that is small enough so that more than one volume is needed.
            Dictionary<string, string> options = new Dictionary<string, string>(this.TestOptions) {["dblock-size"] = "10mb"};

            // First, run two complete backups followed by a partial backup.  We will then set the keep-time
            // option so that the threshold lies between the first and second backups.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                c.Backup(new[] {this.DATAFOLDER});
            }

            // Wait before the second backup so that we can more easily define the keep-time threshold
            // to lie between the first and second backups.
            Thread.Sleep(5000);
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                this.ModifySourceFiles();
                c.Backup(new[] {this.DATAFOLDER});
            }

            // Run a partial backup.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                await this.RunPartialBackup(c);
            }

            // Set the keep-time option so that the threshold lies between the first and second backups
            // and run the delete operation.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                List<IListResultFileset> filesets = c.List().Filesets.ToList();
                DateTime firstBackupTime = filesets[filesets.Count - 1].Time;
                DateTime secondBackupTime = filesets[filesets.Count - 2].Time;
                options["keep-time"] = $"{(DateTime.Now - firstBackupTime).Seconds - (secondBackupTime - firstBackupTime).Seconds / 2}s";
                c.Delete();

                filesets = c.List().Filesets.ToList();
                Assert.AreEqual(2, filesets.Count);
                Assert.AreEqual(BackupType.FULL_BACKUP, filesets[1].IsFullBackup);
                Assert.AreEqual(BackupType.PARTIAL_BACKUP, filesets[0].IsFullBackup);
            }

            // Run another partial backup.  We will then verify that a full backup is retained
            // even when all the "recent" backups are partial.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                await this.RunPartialBackup(c);

                // Set the keep-time option so that the threshold lies after the most recent full backup
                // and run the delete operation.
                options["keep-time"] = "1s";
                c.Delete();

                List<IListResultFileset> filesets = c.List().Filesets.ToList();
                Assert.AreEqual(3, filesets.Count);
                Assert.AreEqual(BackupType.FULL_BACKUP, filesets[2].IsFullBackup);
                Assert.AreEqual(BackupType.PARTIAL_BACKUP, filesets[1].IsFullBackup);
                Assert.AreEqual(BackupType.PARTIAL_BACKUP, filesets[0].IsFullBackup);
            }
        }

        [Test]
        [Category("Disruption")]
        public async Task KeepVersionsRetention()
        {
            // Choose a dblock size that is small enough so that more than one volume is needed.
            Dictionary<string, string> options = new Dictionary<string, string>(this.TestOptions) {["dblock-size"] = "10mb"};

            // Run a full backup.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                c.Backup(new[] {this.DATAFOLDER});
            }

            // Run a partial backup.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                await this.RunPartialBackup(c);
            }

            // Run a partial backup.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                await this.RunPartialBackup(c);
            }

            // Run a full backup.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                this.ModifySourceFiles();
                c.Backup(new[] {this.DATAFOLDER});
            }

            // Run a partial backup.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                options["keep-versions"] = "2";
                await this.RunPartialBackup(c);

                // Partial backups that are followed by a full backup can be deleted.
                List<IListResultFileset> filesets = c.List().Filesets.ToList();
                Assert.AreEqual(3, filesets.Count);
                Assert.AreEqual(BackupType.FULL_BACKUP, filesets[2].IsFullBackup);
                Assert.AreEqual(BackupType.FULL_BACKUP, filesets[1].IsFullBackup);
                Assert.AreEqual(BackupType.PARTIAL_BACKUP, filesets[0].IsFullBackup);
            }
        }

        [Test]
        [Category("Disruption")]
        public async Task StopAfterCurrentFile()
        {
            // Choose a dblock size that is small enough so that more than one volume is needed.
            Dictionary<string, string> options = new Dictionary<string, string>(this.TestOptions) {["dblock-size"] = "10mb"};

            // Run a complete backup.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                c.Backup(new[] {this.DATAFOLDER});
                Assert.AreEqual(1, c.List().Filesets.Count());
                Assert.AreEqual(BackupType.FULL_BACKUP, c.List().Filesets.Single(x => x.Version == 0).IsFullBackup);
            }

            // Run a partial backup.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                await this.RunPartialBackup(c);

                // If we interrupt the backup, the most recent Fileset should be marked as partial.
                Assert.AreEqual(2, c.List().Filesets.Count());
                Assert.AreEqual(BackupType.FULL_BACKUP, c.List().Filesets.Single(x => x.Version == 1).IsFullBackup);
                Assert.AreEqual(BackupType.PARTIAL_BACKUP, c.List().Filesets.Single(x => x.Version == 0).IsFullBackup);
            }

            // Restore files from the partial backup set.
            Dictionary<string, string> restoreOptions = new Dictionary<string, string>(options) {["restore-path"] = this.RESTOREFOLDER};
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, restoreOptions, null))
            {
                IListResults lastResults = c.List("*");
                string[] partialVersionFiles = lastResults.Files.Select(x => x.Path).Where(x => !Utility.IsFolder(x, File.GetAttributes)).ToArray();
                Assert.GreaterOrEqual(partialVersionFiles.Length, 1);
                c.Restore(partialVersionFiles);

                foreach (string filepath in partialVersionFiles)
                {
                    string filename = Path.GetFileName(filepath);
                    Assert.IsTrue(TestUtils.CompareFiles(filepath, Path.Combine(this.RESTOREFOLDER, filename ?? String.Empty), filename, false));
                }
            }

            // Recreating the database should preserve the backup types.
            File.Delete(this.DBFILE);
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                c.Repair();
                Assert.AreEqual(2, c.List().Filesets.Count());
                Assert.AreEqual(BackupType.FULL_BACKUP, c.List().Filesets.Single(x => x.Version == 1).IsFullBackup);
                Assert.AreEqual(BackupType.PARTIAL_BACKUP, c.List().Filesets.Single(x => x.Version == 0).IsFullBackup);
            }

            // Run a complete backup.  Listing the Filesets should include both full and partial backups.
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, options, null))
            {
                c.Backup(new[] {this.DATAFOLDER});
                Assert.AreEqual(3, c.List().Filesets.Count());
                Assert.AreEqual(BackupType.FULL_BACKUP, c.List().Filesets.Single(x => x.Version == 2).IsFullBackup);
                Assert.AreEqual(BackupType.PARTIAL_BACKUP, c.List().Filesets.Single(x => x.Version == 1).IsFullBackup);
                Assert.AreEqual(BackupType.FULL_BACKUP, c.List().Filesets.Single(x => x.Version == 0).IsFullBackup);
            }

            // Restore files from the full backup set.
            restoreOptions["overwrite"] = "true";
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, restoreOptions, null))
            {
                IListResults lastResults = c.List("*");
                string[] fullVersionFiles = lastResults.Files.Select(x => x.Path).Where(x => !Utility.IsFolder(x, File.GetAttributes)).ToArray();
                Assert.AreEqual(this.fileSizes.Length, fullVersionFiles.Length);
                c.Restore(fullVersionFiles);

                foreach (string filepath in fullVersionFiles)
                {
                    string filename = Path.GetFileName(filepath);
                    Assert.IsTrue(TestUtils.CompareFiles(filepath, Path.Combine(this.RESTOREFOLDER, filename ?? String.Empty), filename, false));
                }
            }
        }
    }
}
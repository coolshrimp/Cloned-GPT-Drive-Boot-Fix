using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Management;
using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cloned_GPT_Drive___Boot_Fix
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Constants
        private const string DISKPART_PATH = @"C:\Windows\System32\diskpart.exe";
        private const string CMD_PATH = @"C:\Windows\System32\cmd.exe";
        private const string DISKMGMT_PATH = @"C:\Windows\System32\diskmgmt.msc";

        public string logString = "";


        public MainWindow()
        {
            InitializeComponent();

            // Initialize protected drives collection if null
            if (Properties.Settings.Default.ProtectedDrives == null)
            {
                Properties.Settings.Default.ProtectedDrives = new System.Collections.Specialized.StringCollection();
                Properties.Settings.Default.Save();
            }

            // Check for first run
            if (Properties.Settings.Default.IsFirstRun)
            {
                ShowFirstRunDialog();
                Properties.Settings.Default.IsFirstRun = false;
                Properties.Settings.Default.Save();
            }

            foreach (var Drives in Environment.GetLogicalDrives())
            {
                DriveInfo DriveInf = new DriveInfo(Drives);
                if (DriveInf.IsReady == true)
                {
                    var DriveInfo = DriveInf.Name + "     " + DriveInf.VolumeLabel;
                    DriveSelection_ddbox.Items.Add(DriveInfo);
                }
            }

            ArrayList driveLetters = new ArrayList(26); // Allocate space for alphabet
            for (int i = 65; i < 91; i++) // increment from ASCII values for A-Z
            {
                driveLetters.Add(Convert.ToChar(i)); // Add uppercase letters to possible drive letters
            }

            foreach (string drive in Directory.GetLogicalDrives())
            {
                driveLetters.Remove(drive[0]); // removed used drive letters from possible drive letters
            }

            foreach (char drive in driveLetters)
            {
                UnusedDriveSelection_ddbox.Items.Add(drive + ":\\"); // add unused drive letters to the combo box
            }

            RefreshProtectedDrivesList();

        }

        /// <summary>
        /// Refreshes the protected drives list box from settings
        /// </summary>
        private void RefreshProtectedDrivesList()
        {
            ProtectedDrives_ListBox.Items.Clear();

            if (Properties.Settings.Default.ProtectedDrives != null)
            {
                foreach (string drive in Properties.Settings.Default.ProtectedDrives)
                {
                    ProtectedDrives_ListBox.Items.Add(drive);
                }
            }
        }

        private void RemoveProtection_btn_Click(object sender, RoutedEventArgs e)
        {
            if (ProtectedDrives_ListBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a drive from the protected list to remove.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedDrive = ProtectedDrives_ListBox.SelectedItem.ToString();

            var result = MessageBox.Show(
                $"Are you sure you want to remove protection from {selectedDrive}?\n\nThis drive will then be eligible for cleaning/formatting.",
                "Remove Protection?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                Properties.Settings.Default.ProtectedDrives.Remove(selectedDrive);
                Properties.Settings.Default.Save();
                RefreshProtectedDrivesList();
            }
        }

        private void RefreshProtected_btn_Click(object sender, RoutedEventArgs e)
        {
            RefreshProtectedDrivesList();
        }

        /// <summary>
        /// Adds a drive to the protected list
        /// </summary>
        private void AddProtectedDrive(string driveLetter)
        {
            if (Properties.Settings.Default.ProtectedDrives == null)
            {
                Properties.Settings.Default.ProtectedDrives = new System.Collections.Specialized.StringCollection();
            }

            if (!Properties.Settings.Default.ProtectedDrives.Contains(driveLetter))
            {
                Properties.Settings.Default.ProtectedDrives.Add(driveLetter);
                Properties.Settings.Default.Save();
                RefreshProtectedDrivesList();
            }
        }

        #region Helper Methods

        /// <summary>
        /// Executes diskpart commands and returns the output
        /// </summary>
        private string ExecuteDiskpartCommand(params string[] commands)
        {
            Process p = new Process();
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.FileName = DISKPART_PATH;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.Verb = "runas";
            p.Start();

            foreach (string command in commands)
            {
                p.StandardInput.WriteLine(command);
            }

            p.StandardInput.WriteLine("exit");
            p.StandardInput.Flush();
            p.StandardInput.Close();

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            return output;
        }

        /// <summary>
        /// Checks if a drive letter is in the protected list
        /// </summary>
        private bool IsDriveProtected(string driveLetter)
        {
            if (Properties.Settings.Default.ProtectedDrives == null)
                return false;

            string normalizedLetter = driveLetter.Substring(0, 1).ToUpper();

            foreach (string protectedDrive in Properties.Settings.Default.ProtectedDrives)
            {
                if (protectedDrive.StartsWith(normalizedLetter))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Shows first run dialog to protect system drives
        /// </summary>
        private void ShowFirstRunDialog()
        {
            StringBuilder message = new StringBuilder();
            message.AppendLine("Welcome to Cloned GPT Drive - Boot Fix!");
            message.AppendLine();
            message.AppendLine("For your safety, this tool will protect your system drives.");
            message.AppendLine("The following drives have been detected:");
            message.AppendLine();

            List<string> systemDrives = new List<string>();

            foreach (var drive in Environment.GetLogicalDrives())
            {
                try
                {
                    DriveInfo driveInfo = new DriveInfo(drive);
                    if (driveInfo.IsReady)
                    {
                        // Check if it's the system drive or a drive with Windows installed
                        bool isSystemDrive = drive.StartsWith(System.IO.Path.GetPathRoot(Environment.SystemDirectory));
                        bool hasWindows = Directory.Exists(System.IO.Path.Combine(drive, "Windows"));

                        if (isSystemDrive || hasWindows)
                        {
                            systemDrives.Add(drive);
                            message.AppendLine($"  {drive} - {driveInfo.VolumeLabel} ({FormatBytes(driveInfo.TotalSize)}) [SYSTEM]");
                        }
                        else
                        {
                            message.AppendLine($"  {drive} - {driveInfo.VolumeLabel} ({FormatBytes(driveInfo.TotalSize)})");
                        }
                    }
                }
                catch { }
            }

            message.AppendLine();
            message.AppendLine("System drives will be automatically protected from formatting.");
            message.AppendLine("You can manage protected drives in the Extras tab.");

            MessageBox.Show(message.ToString(), "First Run - Drive Protection", MessageBoxButton.OK, MessageBoxImage.Information);

            // Add system drives to protected list
            foreach (string systemDrive in systemDrives)
            {
                if (!Properties.Settings.Default.ProtectedDrives.Contains(systemDrive))
                {
                    Properties.Settings.Default.ProtectedDrives.Add(systemDrive);
                }
            }

            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Formats bytes to human-readable format
        /// </summary>
        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        /// <summary>
        /// Shows a confirmation dialog before destructive operations
        /// </summary>
        private bool ConfirmDestructiveOperation(string diskInfo, string operation)
        {
            string message = $"WARNING: You are about to {operation}:\n\n{diskInfo}\n\nThis operation will DESTROY ALL DATA on this disk.\n\nAre you absolutely sure?";
            return MessageBox.Show(message, "Confirm Destructive Operation", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        #endregion

        public static string DeleteLines(string s, int linesToRemove)
        {
            return s.Split(Environment.NewLine.ToCharArray(),
                           linesToRemove + 1
                ).Skip(linesToRemove)
                .FirstOrDefault();
        }

        private void FixBoot_btn_Click(object sender, RoutedEventArgs e)
        {

            StatusBox.Text = "";
            StatusBox.AppendText(" Assigning Drive Letter");


            Process p = new Process();                                    // new instance of Process class
            p.StartInfo.UseShellExecute = false;                          // do not start a new shell
            p.StartInfo.RedirectStandardOutput = true;                    // Redirects the on screen results
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.FileName = @"C:\Windows\System32\diskpart.exe";   // executable to run
            p.StartInfo.RedirectStandardInput = true;                     // Redirects the input commands
            p.StartInfo.Verb = "runas";
            p.Start();                                                    // Starts the process
            p.StandardInput.WriteLine("select " + Volumes_dd.SelectedItem.ToString());                        // Issues commands to diskpart
            p.StandardInput.WriteLine("assign letter " + UnusedDriveSelection_ddbox.SelectedItem.ToString().Substring(0, 1));     // Issues commands to diskpart
            p.StandardInput.WriteLine("exit");                            // _\|/_
            p.StandardInput.Flush();
            p.StandardInput.Close();

            string output = p.StandardOutput.ReadToEnd();                 // Places the output to a variable
            p.WaitForExit();
            
            CMDOutputBox.AppendText(DeleteLines(output, 11));

            if (output.Contains("successfully assigned the drive letter") == true)
            {
                StatusBox.AppendText("\r\r Drive Letter Assigned");
            } else {
                StatusBox.AppendText("\r\r Failed to assign drive letter");
                return;
            }


            StatusBox.AppendText("\r\r Fixing Boot Files");

            Process p1 = new Process();                                   // new instance of Process class
            p1.StartInfo.UseShellExecute = false;                          // do not start a new shell
            p1.StartInfo.RedirectStandardOutput = true;                    // Redirects the on screen results
            p1.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p1.StartInfo.CreateNoWindow = true;
            p1.StartInfo.FileName = @"C:\Windows\System32\cmd.exe";   // executable to run
            p1.StartInfo.RedirectStandardInput = true;                     // Redirects the input commands
            p1.StartInfo.Verb = "runas";
            p1.Start();                                                   // Starts the process
            p1.StandardInput.WriteLine("cd C:\\Windows\\system32\\");                           // _\|/_
            p1.StandardInput.WriteLine("bcdboot " + DriveSelection_ddbox.SelectedItem.ToString().Substring(0, 3) + "Windows " + "/s " + UnusedDriveSelection_ddbox.SelectedItem.ToString().Substring(0, 2).ToLower() + " /f UEFI");                            // _\|/_
            p1.StandardInput.WriteLine("exit");
            p1.StandardInput.Flush();
            p1.StandardInput.Close();

            string output1 = p1.StandardOutput.ReadToEnd();                 // Places the output to a variable
            p1.WaitForExit();

            if (output1.Contains("Boot files successfully created.") == true)
            {
                StatusBox.AppendText("\r\r Boot Files Repaired");
            } else {
                var cmdRan = "bcdboot " + DriveSelection_ddbox.SelectedItem.ToString().Substring(0, 3) + "Windows " + "/s " + UnusedDriveSelection_ddbox.SelectedItem.ToString().Substring(0, 2).ToLower() + " /f UEFI";
                StatusBox.AppendText("\r\r Failed to repair boot Files \r\r" + cmdRan);
                return;
            }

            CMDOutputBox.AppendText(DeleteLines(output1, 11));


            StatusBox.AppendText("\r\r Removing Drive Letter");

            Process p2 = new Process();                                    // new instance of Process class
            p2.StartInfo.UseShellExecute = false;                          // do not start a new shell
            p2.StartInfo.RedirectStandardOutput = true;                    // Redirects the on screen results
            p2.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p2.StartInfo.CreateNoWindow = true;
            p2.StartInfo.FileName = @"C:\Windows\System32\diskpart.exe";   // executable to run
            p2.StartInfo.RedirectStandardInput = true;                     // Redirects the input commands
            p2.StartInfo.Verb = "runas";
            p2.Start();                                                    // Starts the process
            p2.StandardInput.WriteLine("select " + Volumes_dd.SelectedItem.ToString());                        // Issues commands to diskpart            
            p2.StandardInput.WriteLine("remove letter " + UnusedDriveSelection_ddbox.SelectedItem.ToString().Substring(0, 1));     // Issues commands to diskpart
            p2.StandardInput.WriteLine("exit");
            p2.StandardInput.Flush();
            p2.StandardInput.Close();

            string output2 = p2.StandardOutput.ReadToEnd();                 // Places the output to a variable
            p2.WaitForExit();                                              // Waits for the exe to finish


            if (output2.Contains("successfully removed the drive letter") == true)
            {
                StatusBox.AppendText("\r\r Drive Letter Removed");
            } else {
                StatusBox.AppendText("\r\r Failed to remove drive letter");
                return;
            }

            CMDOutputBox.AppendText(DeleteLines(output2, 11));



        }

        private void DriveSelection_ddbox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveSelection_ddbox.SelectedItem == null)
                return;

            try
            {
                string driveSelected = DriveSelection_ddbox.SelectedItem.ToString().Substring(0, 3);
                DriveInfo driveInfo = new DriveInfo(driveSelected);
                string driveLetter = DriveSelection_ddbox.SelectedItem.ToString().Substring(0, 1);

                ManagementObject disk = new ManagementObject("win32_logicaldisk.deviceid=\"" + driveLetter + ":\"");
                disk.Get();

                StringBuilder info = new StringBuilder();
                info.AppendLine("Drive Letter:");
                info.AppendLine(driveSelected);
                info.AppendLine();
                info.AppendLine("Drive Label:");
                info.AppendLine(driveInfo.VolumeLabel);
                info.AppendLine();
                info.AppendLine("Drive ID:");
                info.AppendLine(disk["VolumeSerialNumber"].ToString());
                info.AppendLine();
                info.AppendLine("Drive Size:");
                info.AppendLine(FormatBytes(driveInfo.TotalSize));
                info.AppendLine();
                info.AppendLine("Free Space:");
                info.AppendLine(FormatBytes(driveInfo.AvailableFreeSpace));

                Drive_Info.Text = info.ToString();
            }
            catch (Exception ex)
            {
                Drive_Info.Text = "Error reading drive information:\r\n" + ex.Message;
            }
        }

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusBox.Text = "Getting volumes...";

                string listoutput = ExecuteDiskpartCommand("list vol");

                StatusBox.Text = "";
                StatusBox.AppendText(DeleteLines(listoutput, 11));
                CMDOutputBox.AppendText(DeleteLines(listoutput, 11));

                Volumes_dd.Items.Clear();

                for (int i = 0; i < 20; i++)
                {
                    if (StatusBox.Text.Contains("Volume " + i))
                    {
                        Volumes_dd.Items.Add("Volume " + i);
                    }
                }

                if (Volumes_dd.Items.Count > 0)
                {
                    StatusBox.AppendText("\r\r" + Volumes_dd.Items.Count + " volume(s) found.");
                }
                else
                {
                    StatusBox.AppendText("\r\rNo volumes found.");
                }
            }
            catch (Exception ex)
            {
                StatusBox.Text = "Error getting volumes: " + ex.Message;
                MessageBox.Show("Failed to get volumes. Make sure you run this application as Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_Click_2(object sender, RoutedEventArgs e)
        {
           
        }

        private void unmount_disk_btn_Click(object sender, RoutedEventArgs e)
        {

        }

        private void CreatorLink_MouseDown(object sender, MouseButtonEventArgs e)
        {
            System.Diagnostics.Process.Start("https://CoolshrimpModz.com");
        }

        private void Get_Disks_btn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ExtrasInfoBox.Text = "Getting disks...";

                string listdisksoutput = ExecuteDiskpartCommand("list disk");

                ExtrasInfoBox.Text = "";
                ExtrasInfoBox.AppendText(DeleteLines(listdisksoutput, 11));

                Disk_list_dd.Items.Clear();

                for (int i = 0; i < 20; i++)
                {
                    if (ExtrasInfoBox.Text.Contains("Disk " + i))
                    {
                        Disk_list_dd.Items.Add("Disk " + i);
                    }
                }

                if (Disk_list_dd.Items.Count > 0)
                {
                    ExtrasInfoBox.AppendText("\r\r" + Disk_list_dd.Items.Count + " disk(s) found.");
                }
                else
                {
                    ExtrasInfoBox.AppendText("\r\rNo disks found.");
                }
            }
            catch (Exception ex)
            {
                ExtrasInfoBox.Text = "Error getting disks: " + ex.Message;
                MessageBox.Show("Failed to get disks. Make sure you run this application as Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ProtectDisk_btn_Click(object sender, RoutedEventArgs e)
        {
            if (Disk_list_dd.SelectedItem == null)
            {
                MessageBox.Show("Please select a disk first.", "No Disk Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedDisk = Disk_list_dd.SelectedItem.ToString();

            try
            {
                string diskOutput = ExecuteDiskpartCommand($"select {selectedDisk}", "list volume");

                // Find drive letters belonging to this disk
                List<string> drivesOnDisk = new List<string>();
                foreach (var driveLetterObj in Environment.GetLogicalDrives())
                {
                    string letter = driveLetterObj.Substring(0, 1).ToUpper();
                    if (diskOutput.Contains($" {letter} "))
                    {
                        drivesOnDisk.Add(driveLetterObj);
                    }
                }

                if (drivesOnDisk.Count == 0)
                {
                    MessageBox.Show($"No assigned drive letters were found on {selectedDisk}.\n\nOnly disks with visible drive letters can be protected.", 
                        "No Drives Found", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                StringBuilder message = new StringBuilder();
                message.AppendLine($"The following drives on {selectedDisk} will be protected:");
                message.AppendLine();
                foreach (string drive in drivesOnDisk)
                {
                    message.AppendLine($"  • {drive}");
                }
                message.AppendLine();
                message.AppendLine("Protected drives cannot be cleaned or converted through this tool.");

                if (MessageBox.Show(message.ToString(), "Protect Disk?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    foreach (string drive in drivesOnDisk)
                    {
                        AddProtectedDrive(drive);
                    }
                    MessageBox.Show($"{drivesOnDisk.Count} drive(s) are now protected.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to protect disk: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConvertToGPT_btn_Click(object sender, RoutedEventArgs e)
        {
            // Validate selection
            if (Disk_list_dd.SelectedItem == null)
            {
                MessageBox.Show("Please select a disk first.", "No Disk Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedDisk = Disk_list_dd.SelectedItem.ToString();

            // Check if any drive on this disk is protected
            string diskOutput = ExecuteDiskpartCommand($"select {selectedDisk}", "list volume");

            bool hasProtectedDrive = false;
            foreach (string protectedDrive in Properties.Settings.Default.ProtectedDrives)
            {
                if (diskOutput.Contains(protectedDrive.Substring(0, 1)))
                {
                    hasProtectedDrive = true;
                    break;
                }
            }

            if (hasProtectedDrive)
            {
                MessageBox.Show($"Cannot convert {selectedDisk} to GPT.\n\nThis disk contains protected system drives.\n\nYou can manage protected drives in the Extras tab.", 
                    "Protected Drive", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Get disk information for confirmation
            StringBuilder diskInfo = new StringBuilder();
            diskInfo.AppendLine($"Disk: {selectedDisk}");
            diskInfo.AppendLine("\nNote: Converting to GPT requires the disk to be empty.");

            if (!ConfirmDestructiveOperation(diskInfo.ToString(), "convert to GPT"))
            {
                return;
            }

            Process p4 = new Process();
            p4.StartInfo.UseShellExecute = false;
            p4.StartInfo.RedirectStandardOutput = true;
            p4.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p4.StartInfo.CreateNoWindow = true;
            p4.StartInfo.FileName = DISKPART_PATH;
            p4.StartInfo.RedirectStandardInput = true;
            p4.StartInfo.Verb = "runas";
            p4.Start();
            p4.StandardInput.WriteLine("select " + selectedDisk);
            p4.StandardInput.WriteLine("convert GPT");
            p4.StandardInput.WriteLine("exit");
            p4.StandardInput.Flush();
            p4.StandardInput.Close();

            string output4 = p4.StandardOutput.ReadToEnd();
            p4.WaitForExit();

            ExtrasInfoBox.Text = "";
            ExtrasInfoBox.AppendText(DeleteLines(output4, 11));

            if (output4.Contains("successfully converted"))
            {
                MessageBox.Show("Disk successfully converted to GPT.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (output4.Contains("The specified disk is not convertible") || output4.Contains("already a GPT disk"))
            {
                MessageBox.Show("The disk cannot be converted. It may already be GPT or contain data.\n\nYou must clean the disk first before converting.", 
                    "Conversion Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("Failed to convert disk to GPT. Check the detailed output.", "Conversion Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Clean_Disk_btn_Click(object sender, RoutedEventArgs e)
        {
            // Validate selection
            if (Disk_list_dd.SelectedItem == null)
            {
                MessageBox.Show("Please select a disk first.", "No Disk Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedDisk = Disk_list_dd.SelectedItem.ToString();

            // Check if any drive on this disk is protected
            string diskOutput = ExecuteDiskpartCommand($"select {selectedDisk}", "list volume");

            List<string> protectedDrivesOnDisk = new List<string>();
            foreach (string protectedDrive in Properties.Settings.Default.ProtectedDrives)
            {
                if (diskOutput.Contains(protectedDrive.Substring(0, 1)))
                {
                    protectedDrivesOnDisk.Add(protectedDrive);
                }
            }

            if (protectedDrivesOnDisk.Count > 0)
            {
                StringBuilder message = new StringBuilder();
                message.AppendLine($"CANNOT CLEAN {selectedDisk}!");
                message.AppendLine();
                message.AppendLine("This disk contains the following PROTECTED drives:");
                foreach (string drive in protectedDrivesOnDisk)
                {
                    message.AppendLine($"  • {drive}");
                }
                message.AppendLine();
                message.AppendLine("Protected drives cannot be cleaned to prevent data loss.");
                message.AppendLine("You can manage protected drives in the Extras tab.");

                MessageBox.Show(message.ToString(), "Protected Drive - Cannot Clean", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Get disk information for better confirmation
            StringBuilder diskInfo = new StringBuilder();
            diskInfo.AppendLine($"Disk: {selectedDisk}");
            diskInfo.AppendLine($"\n{ExtrasInfoBox.Text.Substring(0, Math.Min(200, ExtrasInfoBox.Text.Length))}...");

            if (!ConfirmDestructiveOperation(diskInfo.ToString(), "CLEAN (wipe all data from)"))
            {
                return;
            }

            // Second confirmation with typing requirement
            MessageBoxResult finalConfirm = MessageBox.Show(
                "FINAL WARNING!\n\nThis will PERMANENTLY DELETE ALL DATA!\n\nClick YES only if you are absolutely certain.",
                "Final Confirmation Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop);

            if (finalConfirm != MessageBoxResult.Yes)
            {
                return;
            }

            Process p5 = new Process();
            p5.StartInfo.UseShellExecute = false;
            p5.StartInfo.RedirectStandardOutput = true;
            p5.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p5.StartInfo.CreateNoWindow = true;
            p5.StartInfo.FileName = DISKPART_PATH;
            p5.StartInfo.RedirectStandardInput = true;
            p5.StartInfo.Verb = "runas";
            p5.Start();
            p5.StandardInput.WriteLine("select " + selectedDisk);
            p5.StandardInput.WriteLine("clean");
            p5.StandardInput.WriteLine("exit");
            p5.StandardInput.Flush();
            p5.StandardInput.Close();

            string output5 = p5.StandardOutput.ReadToEnd();
            p5.WaitForExit();

            ExtrasInfoBox.Text = "";
            ExtrasInfoBox.AppendText(DeleteLines(output5, 11));

            if (output5.Contains("successfully cleaned"))
            {
                MessageBox.Show("Disk successfully cleaned.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Failed to clean disk. Check the detailed output.", "Clean Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Show_disk_manager_btn_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(DISKMGMT_PATH);
        }
    }
}

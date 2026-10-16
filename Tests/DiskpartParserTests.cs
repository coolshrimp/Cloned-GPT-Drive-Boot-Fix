using System;
using System.Collections.Generic;
using System.Linq;

namespace Cloned_GPT_Drive___Boot_Fix.Tests
{
    /// <summary>
    /// Tests for DiskpartParser using captured diskpart text. Pure string parsing only —
    /// nothing here runs diskpart or touches a disk. Built and run by GitHub Actions:
    ///   csc /out:ParserTests.exe "Cloned GPT Drive - Boot Fix\DiskpartParser.cs" Tests\DiskpartParserTests.cs
    /// Exit code is the number of failed checks.
    /// </summary>
    internal static class DiskpartParserTests
    {
        private static int _failures;
        private static int _checks;

        private const string Banner =
            "\r\nMicrosoft DiskPart version 10.0.26100.1150\r\n\r\nCopyright (C) Microsoft Corporation.\r\nOn computer: TESTPC\r\n\r\n";

        private const string ListDisk = Banner +
            "DISKPART> \r\n" +
            "  Disk ###  Status         Size     Free     Dyn  Gpt\r\n" +
            "  --------  -------------  -------  -------  ---  ---\r\n" +
            "  Disk 0    Online          931 GB      0 B        *\r\n" +
            "  Disk 1    Online          465 GB  1024 KB        *\r\n" +
            "  Disk 2    No Media           0 B      0 B\r\n" +
            "  Disk 3    Offline        1863 GB      0 B\r\n" +
            "\r\nDISKPART> \r\nLeaving DiskPart...\r\n";

        private const string ListDiskWithSelected = Banner +
            "DISKPART> \r\n" +
            "  Disk ###  Status         Size     Free     Dyn  Gpt\r\n" +
            "  --------  -------------  -------  -------  ---  ---\r\n" +
            "  Disk 0    Online          931 GB      0 B        *\r\n" +
            "* Disk 1    Online          465 GB  1024 KB        *\r\n" +
            "\r\nDISKPART> \r\n";

        private const string CloneDiskDetail = Banner +
            "DISKPART> \r\nDisk 1 is now the selected disk.\r\n\r\n" +
            "DISKPART> \r\nSamsung SSD 870 EVO 500GB\r\n" +
            "Disk ID: {0A1B2C3D-1111-2222-3333-444455556666}\r\n" +
            "Type   : SATA\r\nStatus : Online\r\nPath   : 0\r\nTarget : 0\r\nLUN ID : 0\r\n" +
            "Location Path : PCIROOT(0)#PCI(1700)#ATA(C01T00L00)\r\n" +
            "Current Read-only State : No\r\nRead-only  : No\r\n" +
            "Boot Disk  : No\r\nPagefile Disk  : No\r\nHibernation File Disk  : No\r\nCrashdump Disk  : No\r\nClustered Disk  : No\r\n\r\n" +
            "  Volume ###  Ltr  Label        Fs     Type        Size     Status     Info\r\n" +
            "  ----------  ---  -----------  -----  ----------  -------  ---------  --------\r\n" +
            "  Volume 4     E   Clone        NTFS   Partition    464 GB  Healthy\r\n" +
            "  Volume 5         My Backup    FAT32  Partition    100 MB  Healthy    Hidden\r\n" +
            "\r\nDISKPART> \r\n";

        private const string HostDiskDetail = Banner +
            "DISKPART> \r\nDisk 0 is now the selected disk.\r\n\r\n" +
            "DISKPART> \r\nWD_BLACK SN850X 1000GB\r\n" +
            "Disk ID: {9F8E7D6C-AAAA-BBBB-CCCC-DDDDEEEEFFFF}\r\n" +
            "Type   : NVMe\r\nStatus : Online\r\n" +
            "Boot Disk  : Yes\r\nPagefile Disk  : Yes\r\nHibernation File Disk  : No\r\nCrashdump Disk  : Yes\r\nClustered Disk  : No\r\n\r\n" +
            "  Volume ###  Ltr  Label        Fs     Type        Size     Status     Info\r\n" +
            "  ----------  ---  -----------  -----  ----------  -------  ---------  --------\r\n" +
            "  Volume 0     C   Windows      NTFS   Partition    930 GB  Healthy    Boot\r\n" +
            "  Volume 1                      FAT32  Partition    100 MB  Healthy    System\r\n" +
            "\r\nDISKPART> \r\n";

        private const string SystemVolumeOnlyDetail = Banner +
            "DISKPART> \r\nDisk 2 is now the selected disk.\r\n\r\n" +
            "DISKPART> \r\nKingston DataTraveler\r\n" +
            "Disk ID: 1A2B3C4D\r\n" +
            "Boot Disk  : No\r\nPagefile Disk  : No\r\n\r\n" +
            "  Volume ###  Ltr  Label        Fs     Type        Size     Status     Info\r\n" +
            "  ----------  ---  -----------  -----  ----------  -------  ---------  --------\r\n" +
            "  Volume 7                      FAT32  Partition    260 MB  Healthy    System\r\n" +
            "\r\nDISKPART> \r\n";

        private const string EmptyDiskDetail = Banner +
            "DISKPART> \r\nDisk 3 is now the selected disk.\r\n\r\n" +
            "DISKPART> \r\nST2000DM008-2FR102\r\n" +
            "Disk ID: 00000000\r\n" +
            "Boot Disk  : No\r\nPagefile Disk  : No\r\n\r\n" +
            "There are no volumes.\r\n" +
            "\r\nDISKPART> \r\n";

        private const string InvalidDiskDetail = Banner +
            "DISKPART> \r\nThe disk you specified is not valid.\r\n\r\n" +
            "Please select a valid disk.\r\n\r\n" +
            "DISKPART> \r\nThere is no disk selected.\r\n\r\n" +
            "Please select a disk and try again.\r\n\r\nDISKPART> \r\n";

        private const string GermanDiskDetail = Banner +
            "DISKPART> \r\nDatenträger 1 ist jetzt der gewählte Datenträger.\r\n\r\n" +
            "DISKPART> \r\nSamsung SSD 870 EVO 500GB\r\n" +
            "Datenträger-ID: {0A1B2C3D-1111-2222-3333-444455556666}\r\n" +
            "Startdatenträger  : Nein\r\n\r\n" +
            "  Volume ###  Bst  Bezeichnung  DS     Typ         Größe    Status     Info\r\n" +
            "  ----------  ---  -----------  -----  ----------  -------  ---------  --------\r\n" +
            "  Volume 4     E   Clone        NTFS   Partition    464 GB  Fehlerfre\r\n" +
            "\r\nDISKPART> \r\n";

        private const string ListVolumeThenMessage = Banner +
            "DISKPART> \r\n" +
            "  Volume ###  Ltr  Label        Fs     Type        Size     Status     Info\r\n" +
            "  ----------  ---  -----------  -----  ----------  -------  ---------  --------\r\n" +
            "  Volume 0     C   Windows      NTFS   Partition    930 GB  Healthy    Boot\r\n" +
            "  Volume 1     D   Websites (S  NTFS   Partition    931 GB  Healthy\r\n" +
            "\r\nDISKPART> \r\nVolume 1 is the selected volume.\r\n\r\nDISKPART> \r\n";

        private static int Main()
        {
            TestListDisk();
            TestListDiskWithSelectedRow();
            TestCloneDiskDetail();
            TestHostDiskIsInUse();
            TestSystemVolumeIsInUse();
            TestEmptyDiskIsComplete();
            TestInvalidDiskFailsClosed();
            TestWrongDiskNumberFailsClosed();
            TestNonEnglishOutputFailsClosed();
            TestEmptyOutputFailsClosed();
            TestVolumeTableEndsAtBlankLine();
            TestDiskKey();
            TestProtectedDiskEntry();
            TestSplitDiskSections();

            Console.WriteLine();
            Console.WriteLine($"{_checks - _failures}/{_checks} checks passed");
            return _failures;
        }

        private static void Check(bool condition, string name)
        {
            _checks++;
            if (condition)
            {
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                _failures++;
                Console.WriteLine("  FAIL  " + name);
            }
        }

        private static void TestListDisk()
        {
            List<DiskListEntry> disks = DiskpartParser.ParseDiskList(ListDisk);
            Check(disks.Count == 4, "list disk: four disks parsed");
            Check(disks.Select(d => d.Number).SequenceEqual(new[] { 0, 1, 2, 3 }), "list disk: disk numbers in order");
            Check(disks[0].Size == "931 GB" && disks[0].IsGpt, "list disk: size and GPT flag");
            Check(disks[2].Status == "No Media" && !disks[2].IsGpt, "list disk: status with a space, no GPT flag");
            Check(disks[3].Status == "Offline", "list disk: offline disk kept");
        }

        private static void TestListDiskWithSelectedRow()
        {
            List<DiskListEntry> disks = DiskpartParser.ParseDiskList(ListDiskWithSelected);
            Check(disks.Count == 2 && disks[1].Number == 1, "list disk: row marked '*' still parsed");
            Check(disks[1].Status == "Online" && disks[1].Size == "465 GB", "list disk: '*' row columns intact");
        }

        private static void TestCloneDiskDetail()
        {
            DiskDetail detail = DiskpartParser.ParseDiskDetail(CloneDiskDetail, 1);
            Check(detail.Selected && detail.DetailFound && detail.VolumeTableFound, "detail: selection, detail block and table found");
            Check(detail.IsComplete, "detail: complete");
            Check(detail.Model == "Samsung SSD 870 EVO 500GB", "detail: model read from line before Disk ID");
            Check(detail.DiskId == "{0A1B2C3D-1111-2222-3333-444455556666}", "detail: Disk ID read");
            Check(!detail.IsInUseByWindows, "detail: clone disk not in use by Windows");
            Check(detail.Volumes.Count == 2, "detail: two volumes");
            Check(detail.Letters.SequenceEqual(new[] { "E:\\" }), "detail: only lettered volume listed as a letter");
            Check(detail.Volumes[1].Letter == "" && detail.Volumes[1].Label == "My Backup", "detail: letterless volume with spaced label");
        }

        private static void TestHostDiskIsInUse()
        {
            DiskDetail detail = DiskpartParser.ParseDiskDetail(HostDiskDetail, 0);
            Check(detail.IsComplete, "host disk: complete");
            Check(detail.IsBootDisk && detail.IsPagefileDisk && detail.IsCrashdumpDisk && !detail.IsHibernationDisk, "host disk: flags read");
            Check(detail.IsInUseByWindows, "host disk: in use by Windows");
        }

        private static void TestSystemVolumeIsInUse()
        {
            DiskDetail detail = DiskpartParser.ParseDiskDetail(SystemVolumeOnlyDetail, 2);
            Check(detail.IsComplete && !detail.IsBootDisk, "system volume: complete, not a boot disk");
            Check(detail.IsInUseByWindows, "system volume: disk holding the running System (EFI) volume is in use");
            Check(detail.Letters.Count == 0, "system volume: no letters");
        }

        private static void TestEmptyDiskIsComplete()
        {
            DiskDetail detail = DiskpartParser.ParseDiskDetail(EmptyDiskDetail, 3);
            Check(detail.NoVolumes && !detail.VolumeTableFound, "empty disk: 'There are no volumes' recognised");
            Check(detail.IsComplete && !detail.IsInUseByWindows, "empty disk: complete and not in use");
        }

        private static void TestInvalidDiskFailsClosed()
        {
            DiskDetail detail = DiskpartParser.ParseDiskDetail(InvalidDiskDetail, 5);
            Check(!detail.Selected && !detail.IsComplete, "invalid disk: not complete");
        }

        private static void TestWrongDiskNumberFailsClosed()
        {
            Check(!DiskpartParser.ParseDiskDetail(CloneDiskDetail, 2).IsComplete, "wrong disk: output for Disk 1 rejected when Disk 2 was asked for");
            Check(!DiskpartParser.ParseDiskDetail(CloneDiskDetail, 11).IsComplete, "wrong disk: Disk 1 output rejected for Disk 11");
        }

        private static void TestNonEnglishOutputFailsClosed()
        {
            Check(!DiskpartParser.ParseDiskDetail(GermanDiskDetail, 1).IsComplete, "non-English: unknown text is never treated as safe");
        }

        private static void TestEmptyOutputFailsClosed()
        {
            Check(!DiskpartParser.ParseDiskDetail("", 0).IsComplete, "empty output: not complete");
            Check(!DiskpartParser.ParseDiskDetail(null, 0).IsComplete, "null output: not complete");
            Check(DiskpartParser.ParseDiskList(null).Count == 0, "null list output: no disks");
        }

        private static void TestVolumeTableEndsAtBlankLine()
        {
            List<VolumeEntry> volumes = DiskpartParser.ParseVolumeTable(ListVolumeThenMessage);
            Check(volumes.Count == 2, "list volume: later 'Volume 1 is the selected volume' not parsed as a row");
            Check(volumes[1].Letter == "D" && volumes[1].Label == "Websites (S", "list volume: truncated label kept");
            Check(volumes[0].Info == "Boot", "list volume: Info column read");
        }

        private static void TestDiskKey()
        {
            string key = DiskpartParser.BuildDiskKey(" Samsung SSD 870 EVO 500GB ", "  S6PXNM0T123456  ", 500105249280, @"SCSI\DISK&VEN_SAMSUNG\4&1");
            Check(key == "Samsung SSD 870 EVO 500GB|SN:S6PXNM0T123456|500105249280", "disk key: model, trimmed serial and size");
            Check(key == DiskpartParser.BuildDiskKey("Samsung SSD 870 EVO 500GB", "S6PXNM0T123456", 500105249280, @"USBSTOR\OTHER"),
                "disk key: serial preferred over device path, so moving ports keeps the key");
            Check(key != DiskpartParser.BuildDiskKey("Samsung SSD 870 EVO 500GB", "S6PXNM0T999999", 500105249280, ""), "disk key: different serial, different key");
            Check(DiskpartParser.BuildDiskKey("USB Disk", "", 64023257088, @"USBSTOR\DISK&VEN_X\123") == @"USB Disk|PNP:USBSTOR\DISK&VEN_X\123|64023257088",
                "disk key: falls back to device path without a serial");
            Check(DiskpartParser.BuildDiskKey("USB Disk", null, 0, null) == "", "disk key: empty when the disk cannot be identified");
        }

        private static void TestProtectedDiskEntry()
        {
            var entry = new ProtectedDiskEntry
            {
                Key = "Samsung SSD 870 EVO 500GB|SN:S6PXNM0T123456|500105249280",
                DiskId = "{0A1B2C3D-1111-2222-3333-444455556666}",
                Name = "Samsung\tSSD\r\n(465.76 GB)"
            };
            ProtectedDiskEntry parsed = ProtectedDiskEntry.Parse(entry.ToString());
            Check(parsed.Key == entry.Key && parsed.DiskId == entry.DiskId, "protected entry: key and Disk ID round-trip");
            Check(parsed.Name == "Samsung SSD  (465.76 GB)", "protected entry: tabs/newlines in the name can't break the format");
            Check(ProtectedDiskEntry.Parse("OnlyAKey").Key == "OnlyAKey" && ProtectedDiskEntry.Parse("OnlyAKey").DiskId == "", "protected entry: missing fields tolerated");
            Check(ProtectedDiskEntry.Parse(null).Key == "", "protected entry: null tolerated");
        }

        private static void TestSplitDiskSections()
        {
            string combined = CloneDiskDetail + HostDiskDetail.Replace(Banner, "") + InvalidDiskDetail.Replace(Banner, "");
            var sections = DiskpartParser.SplitDiskSections(combined);
            Check(sections.Count == 2 && sections.ContainsKey(0) && sections.ContainsKey(1), "sections: one per selected disk, failed select gets none");
            Check(DiskpartParser.ParseDiskDetail(sections[1], 1).Model == "Samsung SSD 870 EVO 500GB", "sections: Disk 1 section holds Disk 1's details");
            Check(DiskpartParser.ParseDiskDetail(sections[0], 0).IsInUseByWindows, "sections: Disk 0 section holds Disk 0's details");
            Check(!DiskpartParser.ParseDiskDetail(sections[1], 1).IsInUseByWindows, "sections: Disk 0's flags do not leak into Disk 1");

            var twice = DiskpartParser.SplitDiskSections(CloneDiskDetail + CloneDiskDetail.Replace(Banner, ""));
            Check(twice.ContainsKey(1) && !DiskpartParser.ParseDiskDetail(twice[1], 1).IsComplete, "sections: a disk selected twice is treated as unreadable");
        }
    }
}

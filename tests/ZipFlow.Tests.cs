using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using ZipFlow;

namespace ZipFlow
{
    internal static class Tests
    {
        private sealed class TestCase
        {
            internal readonly string Name;
            internal readonly Action Body;

            internal TestCase(string name, Action body)
            {
                Name = name;
                Body = body;
            }
        }

        private sealed class EntryData
        {
            internal readonly string Name;
            internal readonly byte[] Content;

            internal EntryData(string name, string content)
            {
                Name = name;
                Content = Encoding.UTF8.GetBytes(content);
            }

            internal EntryData(string name, byte[] content)
            {
                Name = name;
                Content = content;
            }
        }

        private sealed class RecordingRemover : ISourceRemover
        {
            private readonly IList<string> order;
            internal Exception RemoveFailure;
            internal int Calls;

            internal RecordingRemover(IList<string> order)
            {
                this.order = order;
            }

            public void Remove(string path)
            {
                Calls++;
                order.Add("remove");
                if (RemoveFailure != null)
                {
                    throw RemoveFailure;
                }

                File.Delete(path);
            }
        }

        private sealed class RecordingLauncher : IFolderLauncher
        {
            private readonly IList<string> order;
            internal Exception OpenFailure;
            internal int Calls;
            internal string LastPath;

            internal RecordingLauncher(IList<string> order)
            {
                this.order = order;
            }

            public void Open(string path)
            {
                Calls++;
                LastPath = path;
                order.Add("open");
                if (OpenFailure != null)
                {
                    throw OpenFailure;
                }

            }
        }

        private static readonly List<string> TempRoots = new List<string>();

        public static int Main()
        {
            TestCase[] cases = new TestCase[]
            {
                new TestCase("safe_nested_archive_is_verified_removed_then_opened", SafeNestedArchiveIsVerifiedRemovedThenOpened),
                new TestCase("existing_output_is_preserved_and_suffix_is_used", ExistingOutputIsPreservedAndSuffixIsUsed),
                new TestCase("traversal_is_rejected_for_both_separators", TraversalIsRejectedForBothSeparators),
                new TestCase("rooted_unc_and_drive_paths_are_rejected", RootedUncAndDrivePathsAreRejected),
                new TestCase("ads_device_and_trailing_dot_names_are_rejected", AdsDeviceAndTrailingDotNamesAreRejected),
                new TestCase("case_duplicate_and_file_directory_conflicts_are_rejected", CaseDuplicateAndFileDirectoryConflictsAreRejected),
                new TestCase("quota_violations_are_rejected_before_publish", QuotaViolationsAreRejectedBeforePublish),
                new TestCase("full_path_limit_uses_collision_selected_final_root", FullPathLimitUsesCollisionSelectedFinalRoot),
                new TestCase("crc_mismatch_is_rejected_and_source_is_preserved", CrcMismatchIsRejectedAndSourceIsPreserved),
                new TestCase("directory_with_nonzero_crc_is_rejected_before_publish", DirectoryWithNonzeroCrcIsRejectedBeforePublish),
                new TestCase("staging_directory_is_hidden_before_extraction", StagingDirectoryIsHiddenBeforeExtraction),
                new TestCase("post_move_visibility_failure_preserves_published_output_and_source", PostMoveVisibilityFailurePreservesPublishedOutputAndSource),
                new TestCase("source_identity_change_after_publish_suppresses_remove_and_open", SourceIdentityChangeAfterPublishSuppressesRemoveAndOpen),
                new TestCase("same_metadata_source_replacement_suppresses_remove_and_open", SameMetadataSourceReplacementSuppressesRemoveAndOpen),
                new TestCase("encrypted_archive_is_rejected", EncryptedArchiveIsRejected),
                new TestCase("unsupported_general_purpose_flags_are_rejected", UnsupportedGeneralPurposeFlagsAreRejected),
                new TestCase("zip64_sentinel_is_rejected", Zip64SentinelIsRejected),
                new TestCase("multidisk_archive_is_rejected", MultidiskArchiveIsRejected),
                new TestCase("malformed_central_directory_bounds_are_rejected", MalformedCentralDirectoryBoundsAreRejected),
                new TestCase("corrupt_archive_cleans_staging_and_preserves_source", CorruptArchiveCleansStagingAndPreservesSource),
                new TestCase("cleanup_failure_reports_retained_staging_path", CleanupFailureReportsRetainedStagingPath),
                new TestCase("reparse_point_in_staging_is_rejected_before_write", ReparsePointInStagingIsRejectedBeforeWrite),
                new TestCase("delete_failure_keeps_source_and_suppresses_open", DeleteFailureKeepsSourceAndSuppressesOpen),
                new TestCase("open_failure_keeps_published_output_after_source_removal", OpenFailureKeepsPublishedOutputAfterSourceRemoval),
                new TestCase("empty_archive_publishes_an_empty_folder", EmptyArchivePublishesAnEmptyFolder),
                new TestCase("invalid_command_lines_do_not_remove_or_open", InvalidCommandLinesDoNotRemoveOrOpen),
                new TestCase("program_error_formatter_reports_existing_source_zip", ProgramErrorFormatterReportsExistingSourceZip),
                new TestCase("program_error_formatter_does_not_duplicate_source_zip_state", ProgramErrorFormatterDoesNotDuplicateSourceZipState),
                new TestCase("program_error_formatter_recognizes_core_source_archive_state", ProgramErrorFormatterRecognizesCoreSourceArchiveState)
            };

            int failures = 0;
            try
            {
                foreach (TestCase test in cases)
                {
                    try
                    {
                        test.Body();
                        Console.WriteLine("PASS " + test.Name);
                    }
                    catch (Exception exception)
                    {
                        failures++;
                        Console.Error.WriteLine("FAIL " + test.Name + ": " + exception);
                    }
                }
            }
            finally
            {
                foreach (string root in TempRoots)
                {
                    TryDeleteDirectory(root);
                }
            }

            Console.WriteLine(cases.Length - failures + "/" + cases.Length + " tests passed");
            return failures == 0 ? 0 : 1;
        }

        private static void SafeNestedArchiveIsVerifiedRemovedThenOpened()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "holiday.zip", new EntryData[]
            {
                new EntryData("notes.txt", "hello"),
                new EntryData("nested/deeper/data.bin", new byte[] { 0, 1, 2, 3, 255 })
            });
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);

            string result = new ZipProcessor(ArchivePolicy.Default, remover, launcher).Process(archive, destination);

            AssertEqual(Path.Combine(destination, "holiday"), result, "final path");
            AssertEqual("hello", File.ReadAllText(Path.Combine(result, "notes.txt")), "first file content");
            AssertBytes(new byte[] { 0, 1, 2, 3, 255 }, File.ReadAllBytes(Path.Combine(result, "nested", "deeper", "data.bin")));
            AssertFalse(File.Exists(archive), "source should be removed");
            AssertSequence(new string[] { "remove", "open" }, order, "callback order");
            AssertEqual(result, launcher.LastPath, "opened path");
            AssertNoPartial(destination);
        }

        private static void ExistingOutputIsPreservedAndSuffixIsUsed()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string existing = Path.Combine(destination, "files");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.txt"), "untouched");
            string archive = CreateArchive(root, "files.zip", new EntryData[] { new EntryData("new.txt", "new") });
            List<string> order = new List<string>();

            string result = new ZipProcessor(ArchivePolicy.Default, new RecordingRemover(order), new RecordingLauncher(order)).Process(archive, destination);

            AssertEqual(Path.Combine(destination, "files (2)"), result, "suffix path");
            AssertEqual("untouched", File.ReadAllText(Path.Combine(existing, "keep.txt")), "existing output preserved");
            AssertEqual("new", File.ReadAllText(Path.Combine(result, "new.txt")), "new output content");
            AssertNoPartial(destination);
        }

        private static void TraversalIsRejectedForBothSeparators()
        {
            string[] names = new string[] { "../escape.txt", "..\\escape.txt" };
            foreach (string name in names)
            {
                AssertRejectedArchive("traversal-" + Guid.NewGuid().ToString("N") + ".zip", new EntryData[] { new EntryData(name, "bad") }, ArchivePolicy.Default);
            }
        }

        private static void RootedUncAndDrivePathsAreRejected()
        {
            string[] names = new string[]
            {
                "/rooted.txt",
                "\\\\server\\share\\file.txt",
                "C:\\drive.txt"
            };
            foreach (string name in names)
            {
                AssertRejectedArchive("rooted-" + Guid.NewGuid().ToString("N") + ".zip", new EntryData[] { new EntryData(name, "bad") }, ArchivePolicy.Default);
            }
        }

        private static void AdsDeviceAndTrailingDotNamesAreRejected()
        {
            string[] names = new string[]
            {
                "safe:stream.txt",
                "CON.txt",
                "folder/name.",
                "COM\u00b9.log",
                "LPT\u00b3"
            };
            foreach (string name in names)
            {
                AssertRejectedArchive("unsafe-" + Guid.NewGuid().ToString("N") + ".zip", new EntryData[] { new EntryData(name, "bad") }, ArchivePolicy.Default);
            }
        }

        private static void CaseDuplicateAndFileDirectoryConflictsAreRejected()
        {
            AssertRejectedArchive("duplicate.zip", new EntryData[]
            {
                new EntryData("A.txt", "one"),
                new EntryData("a.TXT", "two")
            }, ArchivePolicy.Default);
            AssertRejectedArchive("conflict.zip", new EntryData[]
            {
                new EntryData("node", "file"),
                new EntryData("node/child.txt", "child")
            }, ArchivePolicy.Default);
        }

        private static void QuotaViolationsAreRejectedBeforePublish()
        {
            AssertRejectedArchive("entries.zip", new EntryData[]
            {
                new EntryData("one.txt", "1"),
                new EntryData("two.txt", "2")
            }, new ArchivePolicy(1, 1024, 2048, 1000, 32, 240));

            AssertRejectedArchive("entry-size.zip", new EntryData[]
            {
                new EntryData("large.txt", "12345")
            }, new ArchivePolicy(10, 4, 2048, 1000, 32, 240));

            AssertRejectedArchive("total-size.zip", new EntryData[]
            {
                new EntryData("one.txt", "123"),
                new EntryData("two.txt", "456")
            }, new ArchivePolicy(10, 10, 5, 1000, 32, 240));

            AssertRejectedArchive("ratio.zip", new EntryData[]
            {
                new EntryData("compressible.txt", new byte[4096])
            }, new ArchivePolicy(10, 8192, 8192, 1, 32, 240));

            AssertRejectedArchive("depth.zip", new EntryData[]
            {
                new EntryData("one/two/three.txt", "x")
            }, new ArchivePolicy(10, 1024, 2048, 1000, 2, 240));
        }

        private static void FullPathLimitUsesCollisionSelectedFinalRoot()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string stem = new string('a', 50);
            string existing = Path.Combine(destination, stem);
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.txt"), "untouched");
            string archive = CreateArchive(root, stem + ".zip", new EntryData[] { new EntryData("file.txt", "content") });
            int maximumPath = Path.Combine(destination, stem, "file.txt").Length;
            ArchivePolicy policy = new ArchivePolicy(10, 1024, 2048, 1000, 32, maximumPath);
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);

            AssertThrows<InvalidDataException>(delegate
            {
                new ZipProcessor(policy, remover, launcher).Process(archive, destination);
            }, "collision-selected final path must obey the full-path limit");

            AssertTrue(File.Exists(archive), "source preserved after full-path rejection");
            AssertEqual("untouched", File.ReadAllText(Path.Combine(existing, "keep.txt")), "existing output preserved");
            AssertFalse(Directory.Exists(Path.Combine(destination, stem + " (2)")), "no over-limit collision output");
            AssertEqual(0, remover.Calls, "remover not called");
            AssertEqual(0, launcher.Calls, "launcher not called");
            AssertEqual(0, order.Count, "no callbacks");
            AssertNoPartial(destination);
        }

        private static void CrcMismatchIsRejectedAndSourceIsPreserved()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "crc.zip", new EntryData[] { new EntryData("data.txt", "integrity") });
            CorruptCentralCrc(archive);
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);

            AssertThrows<InvalidDataException>(delegate
            {
                new ZipProcessor(ArchivePolicy.Default, remover, launcher).Process(archive, destination);
            }, "CRC mismatch should fail");

            AssertFailureState(archive, destination, "crc", remover, launcher, order);
        }

        private static void DirectoryWithNonzeroCrcIsRejectedBeforePublish()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "directory-crc.zip", new EntryData[] { new EntryData("folder/", new byte[0]) });
            CorruptCentralCrc(archive);
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);

            AssertThrows<InvalidDataException>(delegate
            {
                new ZipProcessor(ArchivePolicy.Default, remover, launcher).Process(archive, destination);
            }, "directory CRC must be zero");

            AssertFailureState(archive, destination, "directory-crc", remover, launcher, order);
        }

        private static void StagingDirectoryIsHiddenBeforeExtraction()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "hidden-stage.zip", new EntryData[] { new EntryData("file.txt", "content") });
            List<string> order = new List<string>();
            bool observed = false;
            FileAttributes observedAttributes = FileAttributes.Normal;
            bool publishObserved = false;
            FileAttributes preMoveAttributes = FileAttributes.Normal;
            Action<string> beforeExtraction = delegate(string stagingPath)
            {
                observed = true;
                AssertTrue(Directory.Exists(stagingPath), "staging directory exists before extraction");
                observedAttributes = File.GetAttributes(stagingPath);
            };
            Action<string> beforePublish = delegate(string stagingPath)
            {
                publishObserved = true;
                AssertTrue(Directory.Exists(stagingPath), "staging directory exists immediately before move");
                AssertTrue(File.Exists(Path.Combine(stagingPath, "file.txt")), "extracted content exists immediately before move");
                preMoveAttributes = File.GetAttributes(stagingPath);
            };

            string result = new ZipProcessor(
                ArchivePolicy.Default,
                new RecordingRemover(order),
                new RecordingLauncher(order),
                beforeExtraction,
                beforePublish,
                null).Process(archive, destination);

            AssertTrue(observed, "pre-extraction staging observation occurred");
            AssertTrue((observedAttributes & FileAttributes.Hidden) != 0, "staging directory is hidden before extraction");
            AssertTrue(publishObserved, "pre-move staging observation occurred");
            AssertTrue((preMoveAttributes & FileAttributes.Hidden) != 0, "staging directory remains hidden immediately before move");
            AssertTrue(File.Exists(Path.Combine(result, "file.txt")), "extraction still succeeds");
            AssertTrue((File.GetAttributes(result) & FileAttributes.Hidden) == 0, "published output is visible");
            AssertNoPartial(destination);
        }

        private static void PostMoveVisibilityFailurePreservesPublishedOutputAndSource()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "visibility-fail.zip", new EntryData[] { new EntryData("file.txt", "content") });
            string expectedOutput = Path.Combine(destination, "visibility-fail");
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);
            IOException marker = new IOException("simulated visibility failure");
            Action<string> afterMoveBeforeVisibility = delegate(string publishedPath)
            {
                AssertEqual(expectedOutput, publishedPath, "moved output path");
                AssertTrue(Directory.Exists(publishedPath), "output exists immediately after move");
                throw marker;
            };

            IOException exception = AssertThrows<IOException>(delegate
            {
                new ZipProcessor(
                    ArchivePolicy.Default,
                    remover,
                    launcher,
                    null,
                    null,
                    afterMoveBeforeVisibility,
                    null).Process(archive, destination);
            }, "post-move visibility failure should propagate");

            AssertTrue(exception.Message.IndexOf(expectedOutput, StringComparison.OrdinalIgnoreCase) >= 0, "error names published output");
            AssertTrue(Object.ReferenceEquals(marker, exception.InnerException), "original visibility failure is preserved");
            AssertTrue(File.Exists(archive), "source remains after post-move failure");
            AssertTrue(File.Exists(Path.Combine(expectedOutput, "file.txt")), "published output remains");
            AssertEqual(0, remover.Calls, "remover suppressed after post-move failure");
            AssertEqual(0, launcher.Calls, "launcher suppressed after post-move failure");
            AssertEqual(0, order.Count, "no destructive callbacks after post-move failure");
            AssertNoPartial(destination);
        }

        private static void SourceIdentityChangeAfterPublishSuppressesRemoveAndOpen()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "identity-change.zip", new EntryData[] { new EntryData("file.txt", "content") });
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);
            bool publishedObserved = false;
            Action<string> afterPublish = delegate(string publishedPath)
            {
                publishedObserved = Directory.Exists(publishedPath);
                File.SetLastWriteTimeUtc(archive, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            };

            AssertThrows<IOException>(delegate
            {
                new ZipProcessor(
                    ArchivePolicy.Default,
                    remover,
                    launcher,
                    null,
                    afterPublish).Process(archive, destination);
            }, "source identity change should fail after publish");

            string output = Path.Combine(destination, "identity-change");
            AssertTrue(publishedObserved, "post-publish observation occurred after output existed");
            AssertTrue(File.Exists(archive), "changed source remains");
            AssertTrue(File.Exists(Path.Combine(output, "file.txt")), "published output remains");
            AssertEqual(0, remover.Calls, "changed source is not removed");
            AssertEqual(0, launcher.Calls, "changed source output is not opened");
            AssertEqual(0, order.Count, "no callbacks after source identity change");
            AssertNoPartial(destination);
        }

        private static void SameMetadataSourceReplacementSuppressesRemoveAndOpen()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "identity-replacement.zip", new EntryData[] { new EntryData("file.txt", "content") });
            byte[] originalBytes = File.ReadAllBytes(archive);
            DateTime originalLastWriteUtc = File.GetLastWriteTimeUtc(archive);
            long originalLength = new FileInfo(archive).Length;
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);
            bool replacementCreated = false;
            Action<string> afterPublish = delegate(string publishedPath)
            {
                AssertTrue(Directory.Exists(publishedPath), "output exists before source replacement");
                File.Delete(archive);
                File.WriteAllBytes(archive, originalBytes);
                File.SetLastWriteTimeUtc(archive, originalLastWriteUtc);
                replacementCreated = new FileInfo(archive).Length == originalLength
                    && File.GetLastWriteTimeUtc(archive) == originalLastWriteUtc;
            };

            AssertThrows<IOException>(delegate
            {
                new ZipProcessor(
                    ArchivePolicy.Default,
                    remover,
                    launcher,
                    null,
                    afterPublish).Process(archive, destination);
            }, "same-size/time source replacement should fail identity verification");

            string output = Path.Combine(destination, "identity-replacement");
            AssertTrue(replacementCreated, "replacement preserved original length and timestamp");
            AssertTrue(File.Exists(archive), "replacement source remains");
            AssertTrue(File.Exists(Path.Combine(output, "file.txt")), "published output remains");
            AssertEqual(0, remover.Calls, "replacement is not removed");
            AssertEqual(0, launcher.Calls, "replacement output is not opened");
            AssertEqual(0, order.Count, "no callbacks after source replacement");
            AssertNoPartial(destination);
        }

        private static void EncryptedArchiveIsRejected()
        {
            AssertRejectedMutatedArchive("encrypted.zip", SetEncryptionFlags);
        }

        private static void UnsupportedGeneralPurposeFlagsAreRejected()
        {
            AssertRejectedMutatedArchive("patched-data.zip", SetCompressedPatchFlags);
        }

        private static void Zip64SentinelIsRejected()
        {
            AssertRejectedMutatedArchive("zip64.zip", SetZip64CompressedSizeSentinel);
        }

        private static void MultidiskArchiveIsRejected()
        {
            AssertRejectedMutatedArchive("multidisk.zip", SetMultidiskEocd);
        }

        private static void MalformedCentralDirectoryBoundsAreRejected()
        {
            AssertRejectedMutatedArchive("bad-bounds.zip", CorruptCentralDirectorySize);
        }

        private static void CorruptArchiveCleansStagingAndPreservesSource()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = Path.Combine(root, "broken.zip");
            File.WriteAllBytes(archive, new byte[] { 0x50, 0x4b, 0x03, 0x04, 1, 2, 3 });
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);

            AssertThrows<InvalidDataException>(delegate
            {
                new ZipProcessor(ArchivePolicy.Default, remover, launcher).Process(archive, destination);
            }, "corrupt archive should fail");

            AssertFailureState(archive, destination, "broken", remover, launcher, order);
        }

        private static void CleanupFailureReportsRetainedStagingPath()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "cleanup-fail.zip", new EntryData[] { new EntryData("file.txt", "content") });
            CorruptCentralCrc(archive);
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);
            string retainedStaging = null;
            IOException cleanupMarker = new IOException("simulated cleanup denial");
            Action<string> beforeCleanup = delegate(string stagingPath)
            {
                retainedStaging = stagingPath;
                AssertTrue(Directory.Exists(stagingPath), "staging exists when cleanup begins");
                throw cleanupMarker;
            };

            IOException exception = AssertThrows<IOException>(delegate
            {
                new ZipProcessor(
                    ArchivePolicy.Default,
                    remover,
                    launcher,
                    null,
                    null,
                    null,
                    null,
                    beforeCleanup).Process(archive, destination);
            }, "cleanup failure should report retained staging");

            AssertTrue(retainedStaging != null, "cleanup seam observed staging path");
            AssertTrue(exception.Message.IndexOf(retainedStaging, StringComparison.OrdinalIgnoreCase) >= 0, "error names retained staging path");
            AssertTrue(exception.Message.IndexOf(cleanupMarker.Message, StringComparison.OrdinalIgnoreCase) >= 0, "error includes cleanup failure detail");
            AssertTrue(exception.InnerException is InvalidDataException, "primary CRC failure remains inner cause");
            AssertTrue(Directory.Exists(retainedStaging), "failed cleanup retains staging for recovery");
            AssertTrue(File.Exists(archive), "source remains after extraction and cleanup failures");
            AssertFalse(Directory.Exists(Path.Combine(destination, "cleanup-fail")), "no final output published");
            AssertEqual(0, remover.Calls, "remover suppressed after cleanup failure");
            AssertEqual(0, launcher.Calls, "launcher suppressed after cleanup failure");
            AssertEqual(0, order.Count, "no callbacks after cleanup failure");

            Directory.Delete(retainedStaging, true);
            AssertNoPartial(destination);
        }

        private static void ReparsePointInStagingIsRejectedBeforeWrite()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            string sentinel = Path.Combine(outside, "sentinel.txt");
            File.WriteAllText(sentinel, "untouched");
            string archive = CreateArchive(root, "reparse.zip", new EntryData[] { new EntryData("linked/escape.txt", "bad") });
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);
            Action<string> beforeExtraction = delegate(string stagingPath)
            {
                CreateJunction(Path.Combine(stagingPath, "linked"), outside);
            };

            AssertThrows<InvalidDataException>(delegate
            {
                new ZipProcessor(ArchivePolicy.Default, remover, launcher, beforeExtraction).Process(archive, destination);
            }, "reparse point in staging must be rejected");

            AssertTrue(File.Exists(archive), "source remains after reparse rejection");
            AssertEqual("untouched", File.ReadAllText(sentinel), "outside sentinel remains untouched");
            AssertFalse(File.Exists(Path.Combine(outside, "escape.txt")), "no file written through junction");
            AssertFalse(Directory.Exists(Path.Combine(destination, "reparse")), "no final output published");
            AssertEqual(0, remover.Calls, "remover suppressed after reparse rejection");
            AssertEqual(0, launcher.Calls, "launcher suppressed after reparse rejection");
            AssertEqual(0, order.Count, "no callbacks after reparse rejection");
            AssertNoPartial(destination);
        }

        private static void DeleteFailureKeepsSourceAndSuppressesOpen()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "delete-fail.zip", new EntryData[] { new EntryData("file.txt", "content") });
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            IOException marker = new IOException("simulated delete failure");
            remover.RemoveFailure = marker;
            RecordingLauncher launcher = new RecordingLauncher(order);

            IOException exception = AssertThrows<IOException>(delegate
            {
                new ZipProcessor(ArchivePolicy.Default, remover, launcher).Process(archive, destination);
            }, "delete failure should propagate");

            string output = Path.Combine(destination, "delete-fail");
            AssertTrue(exception.Message.IndexOf(output, StringComparison.OrdinalIgnoreCase) >= 0, "delete error names completed output");
            AssertTrue(exception.Message.IndexOf("source archive remains", StringComparison.OrdinalIgnoreCase) >= 0, "delete error reports retained source");
            AssertTrue(Object.ReferenceEquals(marker, exception.InnerException), "delete inner exception preserved");
            AssertTrue(File.Exists(archive), "source remains on delete failure");
            AssertTrue(File.Exists(Path.Combine(output, "file.txt")), "published output remains on delete failure");
            AssertEqual(0, launcher.Calls, "launcher suppressed");
            AssertSequence(new string[] { "remove" }, order, "callback order");
            AssertNoPartial(destination);
        }

        private static void OpenFailureKeepsPublishedOutputAfterSourceRemoval()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "open-fail.zip", new EntryData[] { new EntryData("file.txt", "content") });
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);
            IOException marker = new IOException("simulated open failure");
            launcher.OpenFailure = marker;

            IOException exception = AssertThrows<IOException>(delegate
            {
                new ZipProcessor(ArchivePolicy.Default, remover, launcher).Process(archive, destination);
            }, "open failure should propagate");

            string output = Path.Combine(destination, "open-fail");
            AssertTrue(exception.Message.IndexOf(output, StringComparison.OrdinalIgnoreCase) >= 0, "open error names completed output");
            AssertTrue(exception.Message.IndexOf("source archive was recycled", StringComparison.OrdinalIgnoreCase) >= 0, "open error reports recycled source");
            AssertTrue(Object.ReferenceEquals(marker, exception.InnerException), "open inner exception preserved");
            AssertFalse(File.Exists(archive), "source removed before open");
            AssertTrue(File.Exists(Path.Combine(output, "file.txt")), "published output remains on open failure");
            AssertSequence(new string[] { "remove", "open" }, order, "callback order");
            AssertNoPartial(destination);
        }

        private static void EmptyArchivePublishesAnEmptyFolder()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, "empty.zip", new EntryData[0]);
            List<string> order = new List<string>();

            string result = new ZipProcessor(ArchivePolicy.Default, new RecordingRemover(order), new RecordingLauncher(order)).Process(archive, destination);

            AssertTrue(Directory.Exists(result), "empty output exists");
            AssertEqual(0, Directory.GetFileSystemEntries(result).Length, "empty output has no entries");
            AssertFalse(File.Exists(archive), "empty source removed");
            AssertSequence(new string[] { "remove", "open" }, order, "callback order");
            AssertNoPartial(destination);
        }

        private static void InvalidCommandLinesDoNotRemoveOrOpen()
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string textFile = Path.Combine(root, "not-an-archive.txt");
            File.WriteAllText(textFile, "text");
            string validArchive = CreateArchive(root, "valid.zip", new EntryData[] { new EntryData("file.txt", "content") });
            string missingArchive = Path.Combine(root, "missing.zip");
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);

            AssertThrows<ArgumentException>(delegate
            {
                Program.Run(new string[0], destination, remover, launcher);
            }, "zero arguments should fail");
            AssertThrows<ArgumentException>(delegate
            {
                Program.Run(new string[] { validArchive, validArchive }, destination, remover, launcher);
            }, "multiple arguments should fail");
            AssertThrows<ArgumentException>(delegate
            {
                Program.Run(new string[] { textFile }, destination, remover, launcher);
            }, "non-ZIP input should fail");
            AssertThrows<FileNotFoundException>(delegate
            {
                Program.Run(new string[] { missingArchive }, destination, remover, launcher);
            }, "missing ZIP input should fail");

            AssertTrue(File.Exists(validArchive), "valid archive remains untouched");
            AssertEqual(0, remover.Calls, "invalid arguments never invoke remover");
            AssertEqual(0, launcher.Calls, "invalid arguments never invoke launcher");
            AssertEqual(0, order.Count, "invalid arguments have no callbacks");
            AssertNoPartial(destination);
        }

        private static void ProgramErrorFormatterReportsExistingSourceZip()
        {
            string root = NewRoot();
            string archive = Path.Combine(root, "format-error.zip");
            File.WriteAllText(archive, "not a real archive");
            string expectedPath = Path.GetFullPath(archive);

            string message = Program.FormatError(new InvalidDataException("Archive validation failed."), archive);

            AssertTrue(message.IndexOf("Archive validation failed.", StringComparison.Ordinal) >= 0, "original error detail preserved");
            AssertTrue(message.IndexOf("Source ZIP remains at: " + expectedPath, StringComparison.Ordinal) >= 0, "existing source state and exact path appended");
            AssertEqual(1, CountOccurrences(message, "Source ZIP"), "source state appears once");
        }

        private static void ProgramErrorFormatterDoesNotDuplicateSourceZipState()
        {
            string root = NewRoot();
            string archive = Path.Combine(root, "already-removed.zip");
            string expectedPath = Path.GetFullPath(archive);
            string detailed = "The source archive was recycled, but the completed output could not be opened."
                + Environment.NewLine
                + Environment.NewLine
                + "Source ZIP no longer exists at: "
                + expectedPath;

            string message = Program.FormatError(new IOException(detailed), archive);

            AssertEqual(detailed, message, "existing detailed source state remains unchanged");
            AssertEqual(1, CountOccurrences(message, "Source ZIP"), "source state is not duplicated");
        }

        private static void ProgramErrorFormatterRecognizesCoreSourceArchiveState()
        {
            string root = NewRoot();
            string archive = Path.Combine(root, "recycled.zip");
            string output = Path.Combine(root, "recycled");
            string coreMessage = "The source archive was recycled, but the completed output could not be opened. It remains at: " + output;

            string message = Program.FormatError(new IOException(coreMessage), archive);

            AssertEqual(coreMessage, message, "core source-state message remains unchanged");
            AssertEqual(1, CountOccurrences(message.ToLowerInvariant(), "source archive"), "one source-state phrase remains");
            AssertEqual(0, CountOccurrences(message, "Source ZIP"), "formatter does not append duplicate source state");
        }

        private static void AssertRejectedArchive(string archiveName, EntryData[] entries, ArchivePolicy policy)
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, archiveName, entries);
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);

            AssertThrows<InvalidDataException>(delegate
            {
                new ZipProcessor(policy, remover, launcher).Process(archive, destination);
            }, "unsafe archive should fail: " + archiveName);

            AssertFailureState(archive, destination, Path.GetFileNameWithoutExtension(archive), remover, launcher, order);
        }

        private static void AssertRejectedMutatedArchive(string archiveName, Action<string> mutator)
        {
            string root = NewRoot();
            string destination = MakeDestination(root);
            string archive = CreateArchive(root, archiveName, new EntryData[] { new EntryData("file.txt", "content") });
            mutator(archive);
            List<string> order = new List<string>();
            RecordingRemover remover = new RecordingRemover(order);
            RecordingLauncher launcher = new RecordingLauncher(order);

            AssertThrows<InvalidDataException>(delegate
            {
                new ZipProcessor(ArchivePolicy.Default, remover, launcher).Process(archive, destination);
            }, "mutated archive should be rejected: " + archiveName);

            AssertFailureState(archive, destination, Path.GetFileNameWithoutExtension(archive), remover, launcher, order);
        }

        private static void AssertFailureState(string archive, string destination, string stem, RecordingRemover remover, RecordingLauncher launcher, IList<string> order)
        {
            AssertTrue(File.Exists(archive), "source preserved");
            AssertFalse(Directory.Exists(Path.Combine(destination, stem)), "no final output");
            AssertEqual(0, remover.Calls, "remover not called");
            AssertEqual(0, launcher.Calls, "launcher not called");
            AssertEqual(0, order.Count, "no callbacks");
            AssertNoPartial(destination);
        }

        private static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "ZipFlow.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            TempRoots.Add(root);
            return root;
        }

        private static string MakeDestination(string root)
        {
            string destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(destination);
            return destination;
        }

        private static string CreateArchive(string root, string name, EntryData[] entries)
        {
            string path = Path.Combine(root, name);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, false))
            {
                foreach (EntryData data in entries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(data.Name, CompressionLevel.Optimal);
                    using (Stream output = entry.Open())
                    {
                        output.Write(data.Content, 0, data.Content.Length);
                    }
                }
            }

            return path;
        }

        private static void CreateJunction(string junctionPath, string targetPath)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            startInfo.Arguments = "/d /c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            using (Process process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new Exception("Could not create test junction. " + output + " " + error);
                }
            }
        }

        private static void CorruptCentralCrc(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int centralOffset = FindSignature(bytes, 0x02014b50U);
            int localOffset = FindSignature(bytes, 0x04034b50U);
            AssertTrue(centralOffset >= 0, "central directory signature exists");
            AssertTrue(localOffset >= 0, "local header signature exists");
            bytes[centralOffset + 16] ^= 0x5a;
            bytes[localOffset + 14] ^= 0x5a;
            File.WriteAllBytes(path, bytes);
        }

        private static void SetEncryptionFlags(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int centralOffset = FindSignature(bytes, 0x02014b50U);
            int localOffset = FindSignature(bytes, 0x04034b50U);
            AssertTrue(centralOffset >= 0 && localOffset >= 0, "ZIP headers exist");
            WriteUInt16(bytes, centralOffset + 8, (ushort)(ReadUInt16(bytes, centralOffset + 8) | 1));
            WriteUInt16(bytes, localOffset + 6, (ushort)(ReadUInt16(bytes, localOffset + 6) | 1));
            File.WriteAllBytes(path, bytes);
        }

        private static void SetCompressedPatchFlags(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int centralOffset = FindSignature(bytes, 0x02014b50U);
            int localOffset = FindSignature(bytes, 0x04034b50U);
            AssertTrue(centralOffset >= 0 && localOffset >= 0, "ZIP headers exist");
            WriteUInt16(bytes, centralOffset + 8, (ushort)(ReadUInt16(bytes, centralOffset + 8) | 0x0020));
            WriteUInt16(bytes, localOffset + 6, (ushort)(ReadUInt16(bytes, localOffset + 6) | 0x0020));
            File.WriteAllBytes(path, bytes);
        }

        private static void SetZip64CompressedSizeSentinel(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int centralOffset = FindSignature(bytes, 0x02014b50U);
            AssertTrue(centralOffset >= 0, "central directory exists");
            WriteUInt32(bytes, centralOffset + 20, UInt32.MaxValue);
            File.WriteAllBytes(path, bytes);
        }

        private static void SetMultidiskEocd(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int eocdOffset = FindLastSignature(bytes, 0x06054b50U);
            AssertTrue(eocdOffset >= 0, "EOCD exists");
            WriteUInt16(bytes, eocdOffset + 4, 1);
            File.WriteAllBytes(path, bytes);
        }

        private static void CorruptCentralDirectorySize(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int eocdOffset = FindLastSignature(bytes, 0x06054b50U);
            AssertTrue(eocdOffset >= 0, "EOCD exists");
            uint size = ReadUInt32(bytes, eocdOffset + 12);
            WriteUInt32(bytes, eocdOffset + 12, size + 1);
            File.WriteAllBytes(path, bytes);
        }

        private static int FindSignature(byte[] bytes, uint signature)
        {
            for (int index = 0; index <= bytes.Length - 4; index++)
            {
                uint value = (uint)(bytes[index]
                    | (bytes[index + 1] << 8)
                    | (bytes[index + 2] << 16)
                    | (bytes[index + 3] << 24));
                if (value == signature)
                {
                    return index;
                }
            }

            return -1;
        }

        private static int FindLastSignature(byte[] bytes, uint signature)
        {
            for (int index = bytes.Length - 4; index >= 0; index--)
            {
                if (ReadUInt32(bytes, index) == signature)
                {
                    return index;
                }
            }

            return -1;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));
        }

        private static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void AssertNoPartial(string destination)
        {
            AssertEqual(0, Directory.GetDirectories(destination, ".zipflow-*.partial").Length, "no staging folders");
        }

        private static T AssertThrows<T>(Action action, string message) where T : Exception
        {
            try
            {
                action();
            }
            catch (T exception)
            {
                return exception;
            }

            throw new Exception(message + "; expected " + typeof(T).FullName);
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Assertion failed: " + message);
            }
        }

        private static void AssertFalse(bool condition, string message)
        {
            AssertTrue(!condition, message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception("Assertion failed: " + message + "; expected <" + expected + ">, actual <" + actual + ">");
            }
        }

        private static void AssertSequence(IList<string> expected, IList<string> actual, string message)
        {
            AssertEqual(expected.Count, actual.Count, message + " count");
            for (int index = 0; index < expected.Count; index++)
            {
                AssertEqual(expected[index], actual[index], message + " at " + index);
            }
        }

        private static void AssertBytes(byte[] expected, byte[] actual)
        {
            AssertEqual(expected.Length, actual.Length, "byte length");
            for (int index = 0; index < expected.Length; index++)
            {
                AssertEqual(expected[index], actual[index], "byte at " + index);
            }
        }

        private static int CountOccurrences(string value, string search)
        {
            int count = 0;
            int offset = 0;
            while (true)
            {
                int found = value.IndexOf(search, offset, StringComparison.Ordinal);
                if (found < 0)
                {
                    return count;
                }

                count++;
                offset = found + search.Length;
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }
    }
}

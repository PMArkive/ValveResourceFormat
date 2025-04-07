using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using GUI.Types.Renderer;
using GUI.Utils;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using ValveResourceFormat.NavMesh;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.TextureDecoders;
using ValveResourceFormat.ValveFont;
using static ValveResourceFormat.ResourceTypes.Texture;

namespace Decompiler
{
    public partial class Decompiler
    {
        private readonly Dictionary<string, string> uniqueSpecialDependancies = [];
        private readonly HashSet<string> unknownEntityKeys = [];

        private readonly Lock ConsoleWriterLock = new();
        private int CurrentFile;
        private int TotalFiles;

        // Options
        private string InputFile;
        private string OutputFile;
        private bool RecursiveSearch;
        private bool RecursiveSearchArchives;
        private bool PrintAllBlocks;
        private string BlockToPrint;
        private bool ShouldPrintBlockContents => PrintAllBlocks || !string.IsNullOrEmpty(BlockToPrint);
        private int MaxParallelismThreads;
        private bool OutputVPKDir;
        private bool VerifyVPKChecksums;
        private bool CachedManifest;
        private bool Decompile;
        private TextureCodec TextureDecodeFlags;
        private string[] FileFilter;
        private bool ListResources;
        private string GltfExportFormat;
        private bool GltfExportAnimations;
        private string[] GltfAnimationFilter;
        private bool GltfExportMaterials;
        private bool GltfExportAdaptTextures;
        private bool GltfExportExtras;
        private bool ToolsAssetInfoShort;

        // The options below are for collecting stats and testing exporting, this is mostly intended for VRF developers, not end users.
        private bool CollectStats;
        private bool StatsPrintFilePaths;
        private bool StatsPrintUniqueDependencies;
        private bool StatsCollectParticles;
        private bool StatsCollectVBIB;
        private bool GltfTest;
        private bool DumpUnknownEntityKeys;

        private string[] ExtFilterList;
        private bool IsInputFolder;

        public static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            var decompiler = new Decompiler();

            ConsoleApp.Version = GetVersion();

            // https://github.com/Cysharp/ConsoleAppFramework
            // Go to definition on this method to see the generated source code
            ConsoleApp.Run(args, decompiler.HandleArguments);
        }

        /// <summary>
        /// A test bed command line interface for the VRF library.
        /// </summary>
        /// <param name="input">-i, Input file to be processed. With no additional arguments, a summary of the input(s) will be displayed.</param>
        /// <param name="output">-o, Output path to write to. If input is a folder (or a VPK), this should be a folder.</param>
        /// <param name="decompile">-d|--vpk_decompile, Decompile supported resource files.</param>
        /// <param name="texture_decode_flags">Decompile textures with the specified decode flags, example: "none", "auto", "foceldr".</param>
        /// <param name="recursive">If specified and given input is a folder, all sub directories will be scanned too.</param>
        /// <param name="recursive_vpk">If specified along with --recursive, will also recurse into VPK archives.</param>
        /// <param name="all">-a, Print the content of each resource block in the file.</param>
        /// <param name="block">-b, Print the content of a specific block, example: DATA, RERL, REDI, NTRO.</param>
        /// <param name="threads">If higher than 1, files will be processed concurrently.</param>
        /// <param name="vpk_dir">Print a list of files in given VPK and information about them.</param>
        /// <param name="vpk_verify">Verify checksums and signatures.</param>
        /// <param name="vpk_cache">Use cached VPK manifest to keep track of updates. Only changed files will be written to disk.</param>
        /// <param name="vpk_extensions">-e, File extension(s) filter, example: "vcss_c,vjs_c,vxml_c".</param>
        /// <param name="vpk_filepath">-f, File path filter, example: "panorama/,sounds/" or "scripts/items/items_game.txt".</param>
        /// <param name="vpk_list">-l, Lists all resources in given VPK. File extension and path filters apply.</param>
        /// <param name="gltf_export_format">Exports meshes/models in given glTF format. Must be either "gltf" or "glb".</param>
        /// <param name="gltf_export_animations">Whether to export model animations during glTF exports.</param>
        /// <param name="gltf_animation_list">Animations to include in the glTF, example "idle,dropped". By default will include all animations.</param>
        /// <param name="gltf_export_materials">Whether to export materials during glTF exports.</param>
        /// <param name="gltf_textures_adapt">Whether to perform any glTF spec adaptations on textures (e.g. split metallic map).</param>
        /// <param name="gltf_export_extras">Export additional Mesh properties into glTF extras</param>
        /// <param name="tools_asset_info_short">Whether to print only file paths for tools_asset_info files.</param>
        /// <param name="stats">Collect stats on all input files and then print them. Use "-i steam" to scan all Steam libraries.</param>
        /// <param name="stats_print_files">When using --stats, print example file names for each stat.</param>
        /// <param name="stats_unique_deps">When using --stats, print all unique dependencies that were found.</param>
        /// <param name="stats_particles">When using --stats, collect particle operators, renderers, emitters, initializers.</param>
        /// <param name="stats_vbib">When using --stats, collect vertex attributes.</param>
        /// <param name="gltf_test">When using --stats, also test glTF export code path for every supported file.</param>
        /// <param name="dump_unknown_entity_keys">When using --stats, save all unknown entity key hashes to unknown_keys.txt.</param>
        private int HandleArguments(
            string input,
            string output = default,
            bool decompile = false,
            string texture_decode_flags = nameof(TextureCodec.Auto),
            bool recursive = false,
            bool recursive_vpk = false,
            bool all = false,
            string block = default,
            int threads = 1,
            bool vpk_dir = false,
            bool vpk_verify = false,
            bool vpk_cache = false,
            string vpk_extensions = default,
            string vpk_filepath = default,
            bool vpk_list = false,

            string gltf_export_format = "gltf",
            bool gltf_export_animations = false,
            string gltf_animation_list = default,
            bool gltf_export_materials = false,
            bool gltf_textures_adapt = false,
            bool gltf_export_extras = false,
            bool tools_asset_info_short = false,

            bool stats = false,
            bool stats_print_files = false,
            bool stats_unique_deps = false,
            bool stats_particles = false,
            bool stats_vbib = false,
            bool gltf_test = false,
            bool dump_unknown_entity_keys = false
        )
        {
            InputFile = stats && input.Equals("steam", StringComparison.OrdinalIgnoreCase) ? "steam" : Path.GetFullPath(input);
            OutputFile = output;
            Decompile = decompile;
            TextureDecodeFlags = Enum.Parse<TextureCodec>(texture_decode_flags, true);
            RecursiveSearch = recursive;
            RecursiveSearchArchives = recursive_vpk;
            PrintAllBlocks = all;
            BlockToPrint = block;
            MaxParallelismThreads = threads;
            OutputVPKDir = vpk_dir;
            VerifyVPKChecksums = vpk_verify;
            CachedManifest = vpk_cache;
            FileFilter = vpk_filepath?.Split(',') ?? [];
            ListResources = vpk_list;

            GltfExportFormat = gltf_export_format;
            GltfExportMaterials = gltf_export_materials;
            GltfExportAnimations = gltf_export_animations;
            GltfAnimationFilter = gltf_animation_list?.Split(',') ?? [];
            GltfExportAdaptTextures = gltf_textures_adapt;
            GltfExportExtras = gltf_export_extras;
            ToolsAssetInfoShort = tools_asset_info_short;

            CollectStats = stats;
            StatsPrintFilePaths = stats_print_files;
            StatsPrintUniqueDependencies = stats_unique_deps;
            StatsCollectParticles = stats_particles;
            StatsCollectVBIB = stats_vbib;
            GltfTest = gltf_test;
            DumpUnknownEntityKeys = dump_unknown_entity_keys;

            if (OutputFile != null)
            {
                OutputFile = Path.GetFullPath(OutputFile);
                OutputFile = FixPathSlashes(OutputFile);
            }

            for (var i = 0; i < FileFilter.Length; i++)
            {
                FileFilter[i] = FixPathSlashes(FileFilter[i]);
            }

            if (vpk_extensions != null)
            {
                ExtFilterList = vpk_extensions.Split(',');
            }

            if (GltfExportFormat != "gltf" && GltfExportFormat != "glb")
            {
                Console.Error.WriteLine("glTF export format must be either 'gltf' or 'glb'.");
                return 1;
            }

            if (!GltfExportAnimations && GltfAnimationFilter.Length > 0)
            {
                Console.Error.WriteLine("glTF animation filter is only valid when exporting animations.");
                return 1;
            }

            if (CollectStats && OutputFile != null)
            {
                Console.Error.WriteLine("Do not use --stats with --output.");
                return 1;
            }

            return Execute();
        }

        private int Execute()
        {
            var paths = new List<string>();

            if (Directory.Exists(InputFile))
            {
                if (OutputFile != null && File.Exists(OutputFile))
                {
                    Console.Error.WriteLine("Output path is an existing file, but input is a folder.");

                    return 1;
                }

                // Make sure we always have a trailing slash for input folders
                if (!InputFile.EndsWith(Path.DirectorySeparatorChar))
                {
                    InputFile += Path.DirectorySeparatorChar;
                }

                IsInputFolder = true;

                var dirs = FindPathsToProcessInFolder(InputFile);

                if (dirs == null)
                {
                    return 1;
                }

                paths.AddRange(dirs);
            }
            else if (File.Exists(InputFile))
            {
                if (RecursiveSearch)
                {
                    Console.Error.WriteLine("File passed in with --recursive option. Either pass in a folder or remove --recursive.");

                    return 1;
                }

                // TODO: Support recursing vpks inside of vpk?
                if (RecursiveSearchArchives && !CollectStats)
                {
                    Console.Error.WriteLine("File passed in with --recursive_vpk option, this is not supported.");

                    return 1;
                }

                paths.Add(InputFile);
            }
            else if (InputFile == "steam")
            {
                IsInputFolder = true;

                var steamPaths = GameFolderLocator.FindSteamLibraryFolderPaths();

                foreach (var path in steamPaths)
                {
                    var filesInPath = FindPathsToProcessInFolder(path);

                    if (filesInPath != null)
                    {
                        paths.AddRange(filesInPath);
                    }
                }

                if (paths.Count == 0)
                {
                    Console.Error.WriteLine("Did not find any Steam libraries.");
                    return 1;
                }
            }
            else if (CollectStats && !string.IsNullOrEmpty(InputFile)) // TODO: Support multiple paths for non --stats too
            {
                var splitPaths = InputFile.Split(',');

                IsInputFolder = true;

                foreach (var path in splitPaths)
                {
                    if (!Directory.Exists(path))
                    {
                        Console.Error.WriteLine($"Folder \"{path}\" does not exist.");
                        return 1;
                    }

                    var filesInPath = FindPathsToProcessInFolder(path);

                    if (filesInPath != null)
                    {
                        paths.AddRange(filesInPath);
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("Input \"{0}\" is not a file or a folder.", InputFile);

                return 1;
            }

            CurrentFile = 0;
            TotalFiles = paths.Count;

            if (MaxParallelismThreads > 1)
            {
                Console.WriteLine("Will use {0} threads concurrently.", MaxParallelismThreads);

                var queue = new ConcurrentQueue<string>(paths);
                var tasks = new List<Task>();

                ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);

                if (workerThreads < MaxParallelismThreads)
                {
                    ThreadPool.SetMinThreads(MaxParallelismThreads, MaxParallelismThreads);
                }

                for (var n = 0; n < MaxParallelismThreads; n++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        while (queue.TryDequeue(out var path))
                        {
                            ProcessFile(path);
                        }
                    }));
                }

                Task.WhenAll(tasks).GetAwaiter().GetResult();
            }
            else
            {
                foreach (var path in paths)
                {
                    ProcessFile(path);
                }
            }

            if (CollectStats)
            {
                Console.WriteLine();
                Console.WriteLine($"Processed {CurrentFile} resources:");

                if (StatsPrintUniqueDependencies)
                {
                    Console.WriteLine();
                    Console.WriteLine("Unique special dependancies:");

                    foreach (var stat in uniqueSpecialDependancies)
                    {
                        Console.WriteLine($"{stat.Key} in {stat.Value}");
                    }
                }
            }

            if (DumpUnknownEntityKeys && unknownEntityKeys.Count > 0)
            {
                File.WriteAllLines("unknown_keys.txt", unknownEntityKeys.Select(x => x.ToString(CultureInfo.InvariantCulture)));
                Console.WriteLine($"Wrote {unknownEntityKeys.Count} unknown entity keys to unknown_keys.txt");
            }

            PrintTextureStats();

            return 0;
        }

        private List<string> FindPathsToProcessInFolder(string path)
        {
            var paths = Directory
                .EnumerateFiles(path, "*.*", RecursiveSearch ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(s =>
                {
                    if (ExtFilterList != null)
                    {
                        foreach (var ext in ExtFilterList)
                        {
                            if (s.EndsWith(ext, StringComparison.Ordinal))
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    return SupportedFileNamesRegex().IsMatch(s);
                })
                .ToList();

            if (RecursiveSearchArchives)
            {
                if (!RecursiveSearch)
                {
                    Console.Error.WriteLine("Option --recursive_vpk must be specified with --recursive.");

                    return null;
                }

                var vpkRegex = VpkArchiveIndexRegex();
                var vpks = Directory
                    .EnumerateFiles(path, "*.vpk", SearchOption.AllDirectories)
                    .Where(s => !vpkRegex.IsMatch(s));

                paths.AddRange(vpks);
            }

            if (paths.Count == 0)
            {
                Console.Error.WriteLine($"Unable to find any \"_c\" compiled files in \"{path}\" folder.");

                if (!RecursiveSearch)
                {
                    Console.Error.WriteLine("Perhaps you should specify --recursive option to scan the input folder recursively.");
                }

                return null;
            }

            return paths;
        }

        private void ProcessFile(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            ProcessFile(path, fs);
        }

        private void ProcessFile(string path, Stream stream, string originalPath = null)
        {
#if false
            lock (ConsoleWriterLock)
            {
                CurrentFile++;

                if (ListResources)
                {
                    // do not print a header
                }
                else if (CollectStats && RecursiveSearch)
                {
                    if (CurrentFile % 1000 == 0)
                    {
                        Console.WriteLine($"Processing file {CurrentFile} out of {TotalFiles} files - {path}");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write($"[{CurrentFile}/{TotalFiles}] ");

                    if (originalPath != null)
                    {
                        if (IsInputFolder && originalPath.StartsWith(InputFile, StringComparison.Ordinal))
                        {
                            Console.Write(originalPath[InputFile.Length..]);
                            Console.Write(" -> ");
                        }
                        else if (originalPath != InputFile)
                        {
                            Console.Write(originalPath);
                            Console.Write(" -> ");
                        }
                    }

                    Console.WriteLine(path);
                    Console.ResetColor();
                }
            }
#endif

            Span<byte> magicData = stackalloc byte[4];

            if (stream.Length >= magicData.Length)
            {
                stream.ReadExactly(magicData);
                stream.Seek(-magicData.Length, SeekOrigin.Current);
            }

            var magic = BitConverter.ToUInt32(magicData);

            switch (magic)
            {
                case Package.MAGIC: ParseVPK(path, stream); return;
                case ShaderFile.MAGIC: ParseVCS(path, stream, originalPath); return;
                case NavMeshFile.MAGIC: ParseNAV(path, stream, originalPath); return;
            }

            // Other types may be handled by FileExtract.TryExtractNonResource
            // TODO: Perhaps move nav into it too

            if (BinaryKV3.IsBinaryKV3(magic))
            {
                ParseKV3(path, stream);
                return;
            }

            var pathExtension = Path.GetExtension(path);

            const uint Source1Vcs = 0x06;
            if (CollectStats && pathExtension == ".vcs" && magic == Source1Vcs)
            {
                return;
            }

            if (pathExtension == ".vfont")
            {
                ParseVFont(path);

                return;
            }
            else if (pathExtension == ".uifont")
            {
                ParseUIFont(path);

                return;
            }
            else if (FileExtract.TryExtractNonResource(stream, path, out var content))
            {
                if (OutputFile != null)
                {
                    var extension = Path.GetExtension(content.FileName);
                    path = Path.ChangeExtension(path, extension);

                    var outFilePath = GetOutputPath(path);
                    DumpContentFile(outFilePath, content);
                }
                else
                {
                    var output = Encoding.UTF8.GetString(content.Data);

                    if (!CollectStats)
                    {
                        Console.WriteLine(output);
                    }
                }
                content.Dispose();

                return;
            }

            using var resource = new Resource
            {
                FileName = path,
            };

            try
            {
                resource.Read(stream);

                var extension = FileExtract.GetExtension(resource);

                if (extension == null)
                {
                    extension = Path.GetExtension(path);

                    if (extension.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.Ordinal))
                    {
                        extension = extension[..^2];
                    }
                }

                if (CollectStats)
                {
                    TestAndCollectStats(resource, path, originalPath);
                }

                if (OutputFile != null)
                {
                    using var fileLoader = new GameFileLoader(null, resource.FileName);

                    using var contentFile = DecompileResource(resource, fileLoader);

                    path = Path.ChangeExtension(path, extension);
                    var outFilePath = GetOutputPath(path);

                    var extensionNew = Path.GetExtension(outFilePath);
                    if (extensionNew.Length == 0 || extensionNew[1..] != extension)
                    {
                        lock (ConsoleWriterLock)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"Extension '.{extension}' might be more suitable than the one provided '{extensionNew}'");
                            Console.ResetColor();
                        }
                    }

                    DumpContentFile(outFilePath, contentFile);
                }
            }
            catch (Exception e)
            {
                LogException(e, path, originalPath);
            }

            if (CollectStats)
            {
                return;
            }

            //Console.WriteLine("\tInput Path: \"{0}\"", args[fi]);
            //Console.WriteLine("\tResource Name: \"{0}\"", "???");
            //Console.WriteLine("\tID: {0:x16}", 0);

            lock (ConsoleWriterLock)
            {
                // Highlight resource type line if undetermined
                if (resource.ResourceType == ResourceType.Unknown)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                }

                Console.WriteLine("\tResource Type: {0} [Version {1}] [Header Version: {2}]", resource.ResourceType, resource.Version, resource.HeaderVersion);
                Console.ResetColor();
            }

            Console.WriteLine("\tFile Size: {0} bytes", resource.FileSize);
            Console.WriteLine(Environment.NewLine);

            if (resource.ContainsBlockType(BlockType.RERL))
            {
                Console.WriteLine("--- Resource External Refs: ---");
                Console.WriteLine("\t{0,-16}  {1,-48}", "Id:", "Resource Name:");

                foreach (var res in resource.ExternalReferences.ResourceRefInfoList)
                {
                    Console.WriteLine("\t{0:X16}  {1,-48}", res.Id, res.Name);
                }
            }
            else
            {
                Console.WriteLine("--- (No External Resource References Found)");
            }

            Console.WriteLine(Environment.NewLine);

            Console.WriteLine("--- Resource Blocks: Count {0} ---", resource.Blocks.Count);

            foreach (var block in resource.Blocks)
            {
                Console.WriteLine("\t-- Block: {0,-4}  Size: {1,-6} bytes [Offset: {2,6}]", block.Type, block.Size, block.Offset);
            }

            if (ShouldPrintBlockContents)
            {
                Console.WriteLine(Environment.NewLine);

                foreach (var block in resource.Blocks)
                {
                    if (!PrintAllBlocks && BlockToPrint != block.Type.ToString())
                    {
                        continue;
                    }

                    Console.WriteLine("--- Data for block \"{0}\" ---", block.Type);
                    Console.WriteLine(block.ToString());
                }
            }
        }

        private ContentFile DecompileResource(Resource resource, IFileLoader fileLoader, IProgress<string> progressReporter = null)
        {
            return resource.ResourceType switch
            {
                ResourceType.Texture => new TextureExtract(resource)
                {
                    DecodeFlags = TextureDecodeFlags,
                }.ToContentFile(),
                _ => FileExtract.Extract(resource, fileLoader, progressReporter),
            };
        }

        private void ParseVCS(string path, Stream stream, string originalPath)
        {
            using var shader = new ShaderFile();

            try
            {
                shader.Read(path, stream);

                if (!CollectStats)
                {
                    shader.PrintSummary();
                }
                else
                {
                    shader.PrintSummary(static (s) => { });

                    if (shader.ZframesLookup.Count > 0)
                    {
                        var zframe = shader.GetZFrameFile(0);
                    }

                    var id = $"Shader version {shader.VcsVersion}";

                    if (originalPath != null)
                    {
                        path = $"{originalPath} -> {path}";
                    }
                }
            }
            catch (Exception e)
            {
                LogException(e, path, originalPath);
            }
        }

        private void ParseNAV(string path, Stream stream, string originalPath)
        {
            try
            {
                var navMeshFile = new NavMeshFile();
                navMeshFile.Read(stream);

                if (!CollectStats)
                {
                    Console.WriteLine(navMeshFile.ToString());
                }
                else
                {
                    navMeshFile.ToString();

                    var id = $"NavMesh version {navMeshFile.Version}, subversion {navMeshFile.SubVersion}";

                    if (originalPath != null)
                    {
                        path = $"{originalPath} -> {path}";
                    }
                }
            }
            catch (Exception e)
            {
                LogException(e, path, originalPath);
            }
        }

        private void ParseVFont(string path) // TODO: Accept Stream
        {
            var font = new ValveFont();

            try
            {
                var output = font.Read(path);

                if (OutputFile != null)
                {
                    path = Path.ChangeExtension(path, "ttf");
                    path = GetOutputPath(path);

                    DumpFile(path, output);
                }
            }
            catch (Exception e)
            {
                LogException(e, path);
            }
        }

        private void ParseUIFont(string path) // TODO: Accept Stream
        {
            var fontPackage = new UIFontFilePackage();

            try
            {
                fontPackage.Read(path);

                if (OutputFile != null)
                {
                    var outputDirectory = Path.GetDirectoryName(path);

                    foreach (var fontFile in fontPackage.FontFiles)
                    {
                        var outputPath = Path.Combine(outputDirectory, fontFile.FileName);
                        DumpFile(outputPath, fontFile.OpenTypeFontData);
                    }
                }
            }
            catch (Exception e)
            {
                LogException(e, path);
            }
        }

        private void ParseKV3(string path, Stream stream)
        {
            var kv3 = new BinaryKV3();

            try
            {
                using (var binaryReader = new BinaryReader(stream))
                {
                    kv3.Size = (uint)stream.Length;
                    kv3.Read(binaryReader);
                }

                Console.WriteLine(kv3.ToString());
            }
            catch (Exception e)
            {
                LogException(e, path);
            }
        }

        private void ParseVPK(string path, Stream stream)
        {
            using var package = new Package();
            package.SetFileName(path);

            try
            {
                package.Read(stream);
            }
            catch (NotSupportedException e)
            {
                lock (ConsoleWriterLock)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Error.WriteLine($"Failed to open vpk '{path}' - {e.Message}");
                    Console.ResetColor();
                }

                return;
            }
            catch (Exception e)
            {
                LogException(e, path);

                return;
            }

            if (VerifyVPKChecksums)
            {
                try
                {
                    VerifyVPK(package);
                }
                catch (Exception e)
                {
                    LogException(e, path);
                }

                return;
            }

            if (OutputFile == null)
            {
                var orderedEntries = package.Entries.OrderByDescending(x => x.Value.Count).ThenBy(x => x.Key).ToList();

                if (ExtFilterList != null)
                {
                    orderedEntries = orderedEntries.Where(x => ExtFilterList.Contains(x.Key)).ToList();
                }
                else if (CollectStats)
                {
                    orderedEntries = orderedEntries.Where(x =>
                    {
                        if (x.Key == "vpk")
                        {
                            return RecursiveSearchArchives;
                        }

                        return SupportedFileNamesRegex().IsMatch(x.Key);
                    }).ToList();
                }

                if (ListResources)
                {
                    var listEntries = orderedEntries.SelectMany(x => x.Value).ToList();
                    listEntries.Sort((a, b) => string.CompareOrdinal(a.GetFullPath(), b.GetFullPath()));

                    foreach (var (entry, _) in FilteredEntries(listEntries))
                    {
                        Console.WriteLine($"{entry.GetFullPath()} CRC:{entry.CRC32:x10} size:{entry.TotalLength}");
                    }

                    return;
                }

                if (!CollectStats)
                {
                    Console.WriteLine("--- Files in package:");
                }

                var processVpkFiles = CollectStats || ShouldPrintBlockContents;

                if (processVpkFiles)
                {
                    var queue = new ConcurrentQueue<PackageEntry>();

                    foreach (var entryGroup in orderedEntries)
                    {
                        foreach (var (entry, _) in FilteredEntries(entryGroup.Value))
                        {
                            queue.Enqueue(entry);
                        }
                    }

                    Interlocked.Add(ref TotalFiles, queue.Count);

                    if (MaxParallelismThreads > 1)
                    {
                        var tasks = new List<Task>();

                        for (var n = 0; n < MaxParallelismThreads; n++)
                        {
                            tasks.Add(Task.Run(() =>
                            {
                                while (queue.TryDequeue(out var file))
                                {
                                    using var entryStream = GameFileLoader.GetPackageEntryStream(package, file);
                                    ProcessFile(file.GetFullPath(), entryStream, path);
                                }
                            }));
                        }

                        Task.WhenAll(tasks).GetAwaiter().GetResult();
                    }
                    else
                    {
                        while (queue.TryDequeue(out var file))
                        {
                            package.ReadEntry(file, out var output);

                            using var entryStream = new MemoryStream(output);
                            ProcessFile(file.GetFullPath(), entryStream, path);
                        }
                    }
                }
                else
                {
                    foreach (var entry in orderedEntries)
                    {
                        Console.WriteLine($"\t{entry.Key}: {entry.Value.Count} files");
                    }
                }
            }
            else
            {
                Console.WriteLine("--- Dumping decompiled files...");

                const string CachedManifestVersionPrefix = "// s2v_version=";
                var manifestPath = string.Concat(path, ".manifest.txt");
                var manifestData = new Dictionary<string, uint>();

                if (CachedManifest && File.Exists(manifestPath))
                {
                    using var file = new StreamReader(manifestPath);
                    string line;
                    var firstLine = true;
                    var goodCachedVersion = false;

                    // add version
                    while ((line = file.ReadLine()) != null)
                    {
                        var lineSpan = line.AsSpan();

                        if (firstLine)
                        {
                            firstLine = false;

                            if (lineSpan.StartsWith(CachedManifestVersionPrefix))
                            {
                                var oldVersion = lineSpan[CachedManifestVersionPrefix.Length..];
                                var newVersion = typeof(Decompiler).Assembly.GetName().Version.ToString();

                                goodCachedVersion = oldVersion.SequenceEqual(newVersion);

                                if (!goodCachedVersion)
                                {
                                    break;
                                }
                            }
                        }

                        var space = lineSpan.IndexOf(' ');

                        if (space > 0 && uint.TryParse(lineSpan[..space], CultureInfo.InvariantCulture, out var hash))
                        {
                            manifestData.Add(lineSpan[(space + 1)..].ToString(), hash);
                        }
                    }

                    if (!goodCachedVersion)
                    {
                        Console.Error.WriteLine("Decompiler version changed, cached manifest will be ignored.");
                        manifestData.Clear();
                    }
                }

                using var fileLoader = new GameFileLoader(package, package.FileName);

                foreach (var type in package.Entries)
                {
                    ProcessVPKEntries(path, package, fileLoader, type.Key, manifestData);
                }

                if (CachedManifest)
                {
                    using var file = new StreamWriter(manifestPath);

                    file.WriteLine($"{CachedManifestVersionPrefix}{typeof(Decompiler).Assembly.GetName().Version}");

                    foreach (var hash in manifestData)
                    {
                        if (package.FindEntry(hash.Key) == null)
                        {
                            Console.WriteLine("\t{0} no longer exists in VPK", hash.Key);
                        }

                        file.WriteLine($"{hash.Value} {hash.Key}");
                    }
                }
            }

            if (OutputVPKDir)
            {
                foreach (var type in package.Entries)
                {
                    foreach (var file in type.Value)
                    {
                        Console.WriteLine(file);
                    }
                }
            }
        }

        private static void VerifyVPK(Package package)
        {
            if (!package.IsSignatureValid())
            {
                throw new InvalidDataException("The signature in this package is not valid.");
            }

            Console.WriteLine("Verifying hashes...");

            package.VerifyHashes();

            var processed = 0;
            var maximum = 1f;

            var progressReporter = new Progress<string>(progress =>
            {
                if (processed++ % 1000 == 0)
                {
                    Console.WriteLine($"[{processed / maximum * 100f,6:#00.00}%] {progress}");
                }
            });

            if (package.ArchiveMD5Entries.Count > 0)
            {
                maximum = package.ArchiveMD5Entries.Count;

                Console.WriteLine("Verifying chunk hashes...");

                package.VerifyChunkHashes(progressReporter);
            }
            else
            {
                maximum = package.Entries.Sum(x => x.Value.Count);

                Console.WriteLine("Verifying file checksums...");

                package.VerifyFileChecksums(progressReporter);
            }

            Console.WriteLine("Success.");
        }

        private void ProcessVPKEntries(string parentPath, Package package,
            IFileLoader fileLoader, string type, Dictionary<string, uint> manifestData)
        {
            var allowSubFilesFromExternalRefs = true;
            if (ExtFilterList != null)
            {
                if (!ExtFilterList.Contains(type))
                {
                    return;
                }

                if (type == "vmat_c" && ExtFilterList.Contains("vmat_c") && !ExtFilterList.Contains("vtex_c"))
                {
                    allowSubFilesFromExternalRefs = false;
                }
            }

            if (!package.Entries.TryGetValue(type, out var entries))
            {
                Console.WriteLine("There are no files of type \"{0}\".", type);

                return;
            }

            var progressReporter = new Progress<string>(progress => Console.WriteLine($"--- {progress}"));
            var gltfModelExporter = new GltfModelExporter(fileLoader)
            {
                ExportAnimations = GltfExportAnimations,
                ExportMaterials = GltfExportMaterials,
                AdaptTextures = GltfExportAdaptTextures,
                ExportExtras = GltfExportExtras,
                ProgressReporter = progressReporter,
            };

            gltfModelExporter.AnimationFilter.UnionWith(GltfAnimationFilter);

            foreach (var (file, filePath) in FilteredEntries(entries))
            {
                var extension = type;

                if (OutputFile != null && CachedManifest)
                {
                    if (manifestData.TryGetValue(filePath, out var oldCrc32) && oldCrc32 == file.CRC32)
                    {
                        continue;
                    }

                    manifestData[filePath] = file.CRC32;
                }

                Console.WriteLine("\t[archive index: {0:D3}] {1}", file.ArchiveIndex, filePath);

                var totalLength = (int)file.TotalLength;
                var rawFileData = ArrayPool<byte>.Shared.Rent(totalLength);

                try
                {
                    package.ReadEntry(file, rawFileData);

                    // Not a file that can be decompiled, or no decompilation was requested
                    if (!Decompile || !type.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.Ordinal))
                    {
                        if (OutputFile != null)
                        {
                            var outputFile = filePath;

                            if (RecursiveSearchArchives)
                            {
                                outputFile = Path.Combine(parentPath, outputFile);
                            }

                            outputFile = GetOutputPath(outputFile, useOutputAsDirectory: true);

                            DumpFile(outputFile, rawFileData.AsSpan()[..totalLength]);
                        }

                        continue;
                    }

                    using var resource = new Resource
                    {
                        FileName = filePath,
                    };
                    using var memory = new MemoryStream(rawFileData, 0, totalLength);

                    resource.Read(memory);

                    extension = FileExtract.GetExtension(resource) ?? type[..^2];

                    // TODO: This is forcing gltf export - https://github.com/ValveResourceFormat/ValveResourceFormat/issues/782
                    if (GltfModelExporter.CanExport(resource) && resource.ResourceType != ResourceType.EntityLump)
                    {
                        var outputExtension = GltfExportFormat;
                        var outputFile = Path.Combine(OutputFile, Path.ChangeExtension(filePath, outputExtension));

                        Directory.CreateDirectory(Path.GetDirectoryName(outputFile));

                        gltfModelExporter.Export(resource, outputFile);

                        continue;
                    }

                    using var contentFile = DecompileResource(resource, fileLoader, progressReporter);

                    if (OutputFile != null)
                    {
                        var outputFile = filePath;

                        if (RecursiveSearchArchives)
                        {
                            outputFile = Path.Combine(parentPath, outputFile);
                        }

                        if (type != extension)
                        {
                            outputFile = Path.ChangeExtension(outputFile, extension);
                        }

                        outputFile = GetOutputPath(outputFile, useOutputAsDirectory: true);

                        DumpContentFile(outputFile, contentFile, allowSubFilesFromExternalRefs);
                    }
                }
                catch (Exception e)
                {
                    LogException(e, filePath, parentPath);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rawFileData);
                }
            }
        }

        private static void DumpContentFile(string path, ContentFile contentFile, bool dumpSubFiles = true)
        {
            if (contentFile.Data != null)
            {
                DumpFile(path, contentFile.Data);
            }

            if (dumpSubFiles)
            {
                foreach (var contentSubFile in contentFile.SubFiles)
                {
                    DumpFile(Path.Combine(Path.GetDirectoryName(path), contentSubFile.FileName), contentSubFile.Extract.Invoke());
                }
            }
        }

        private static void DumpFile(string path, ReadOnlySpan<byte> data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            File.WriteAllBytes(path, data.ToArray());

            Console.WriteLine("--- Dump written to \"{0}\"", path);
        }

        private IEnumerable<(PackageEntry Entry, string FilePath)> FilteredEntries(IEnumerable<PackageEntry> entries)
        {
            foreach (var entry in entries)
            {
                var filePath = FixPathSlashes(entry.GetFullPath());

                if (IsExcludedVpkFilePath(filePath))
                {
                    continue;
                }

                yield return (entry, filePath);
            }
        }

        private bool IsExcludedVpkFilePath(string filePath)
        {
            return FileFilter.Length > 0 && FileFilter.All(filter => !filePath.StartsWith(filter, StringComparison.Ordinal));
        }

        private string GetOutputPath(string inputPath, bool useOutputAsDirectory = false)
        {
            if (IsInputFolder)
            {
                if (!inputPath.StartsWith(InputFile, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Path '{inputPath}' does not start with '{InputFile}', is this a bug?", nameof(inputPath));
                }

                inputPath = inputPath[InputFile.Length..];

                return Path.Combine(OutputFile, inputPath);
            }
            else if (useOutputAsDirectory || Directory.Exists(OutputFile))
            {
                return Path.Combine(OutputFile, inputPath);
            }

            return Path.GetFullPath(OutputFile);
        }

        private Dictionary<string, (int Total, int Perfect, int Good, int Bad)> TextureStats = new();

        private void PrintTextureStats()
        {
            Console.WriteLine($"Files: {CurrentFile} / {TotalFiles}");
            Console.WriteLine("Format          | Total   | Perfect          | Good    | Bad");

            foreach (var (f, s) in TextureStats.OrderByDescending(x => x.Value.Total))
            {
                var perfectPercentage = s.Perfect / (float)s.Total * 100;
                var badPercentage = s.Bad / (float)s.Total * 100;

                Console.WriteLine($"{f,-15} | {s.Total,-7} | {s.Perfect,-7} ({perfectPercentage,5:F1}%) | {s.Good,-7} | {s.Bad,-7} ({badPercentage,5:F1}%)");
            }
        }

        /// <summary>
        /// This method tries to run through all the code paths for a particular resource,
        /// which allows us to quickly find exceptions when running --stats over an entire game folder.
        /// </summary>
        private void TestAndCollectStats(Resource resource, string path, string originalPath)
        {
            if (resource.ResourceType != ResourceType.Texture)
            {
                return;
            }

            var textureData = (Texture)resource.DataBlock;
            var mipLevel = (uint)Math.Max(textureData.NumMipLevels - 1, 0);
            var depth = (uint)Math.Max(textureData.Depth / 2, 0);
            var cube = (textureData.Flags & VTexFlags.CUBE_TEXTURE) == 0 ? CubemapFace.PositiveX : CubemapFace.PositiveZ;

            using var gpuBitmap = textureData.GenerateBitmap(depth, cube, mipLevel, TextureCodec.None, gpuDecoder: HardwareAcceleratedTextureDecoder.Decoder);
            using var cpuBitmap = textureData.GenerateBitmap(depth, cube, mipLevel, TextureCodec.None, gpuDecoder: null);

            // COMPARE
            Debug.Assert(cpuBitmap.Width == gpuBitmap.Width && cpuBitmap.Height == gpuBitmap.Height, "GPU and CPU bitmaps have different sizes");
            Debug.Assert(cpuBitmap.ColorType == gpuBitmap.ColorType, "GPU and CPU bitmaps have different color types");

            var errorCount = 0;
            var errorCountPrecise = 0;

            for (var y = 0; y < cpuBitmap.Height; y++)
            {
                for (var x = 0; x < cpuBitmap.Width; x++)
                {
                    var gpuPixel = GLTextureViewer.GetFixedColor(textureData.Format, gpuBitmap, x, y);
                    var cpuPixel = GLTextureViewer.GetFixedColor(textureData.Format, cpuBitmap, x, y);

                    if (gpuPixel != cpuPixel)
                    {
                        errorCountPrecise++;
                    }

                    if (Math.Abs(gpuPixel.Red - cpuPixel.Red) > 7
                    || Math.Abs(gpuPixel.Green - cpuPixel.Green) > 7
                    || Math.Abs(gpuPixel.Blue - cpuPixel.Blue) > 7
                    || Math.Abs(gpuPixel.Alpha - cpuPixel.Alpha) > 7)
                    {
                        //Log.Error(nameof(GLTextureViewer), $"GPU and CPU bitmaps have different pixels at ({x}, {y})");
                        errorCount++;
                    }
                }
            }

            var marginalPercentage = errorCount * 100f / cpuBitmap.Pixels.Length;
            var exactPercentage = errorCountPrecise * 100f / cpuBitmap.Pixels.Length;

            if (originalPath != null)
            {
                path = $"{originalPath} -> {path}";
            }

            var key = $"{CurrentFile}/{TotalFiles}";
            var result = $"[{textureData.Format}] GPU and CPU bitmaps have {marginalPercentage}% different pixels ({100 - exactPercentage}% exact pixels) file: {path}";

            lock (ConsoleWriterLock)
            {
                if (!TextureStats.TryGetValue(textureData.Format.ToString(), out var stats))
                {
                    stats = (0, 0, 0, 0);
                }

                stats.Total++;

                if (errorCountPrecise == 0)
                {
                    stats.Perfect++;
                }
                else if (errorCount == 0)
                {
                    stats.Good++;
                    //Log.Debug(key, result);
                }
                else
                {
                    stats.Bad++;
                    Log.Error(key, result);
                }

                TextureStats[textureData.Format.ToString()] = stats;

                if (CurrentFile++ % 1000 == 0)
                {
                    PrintTextureStats();
                }
            }
        }

        private void LogException(Exception e, string path, string parentPath = null)
        {
            var exceptionsFileName = CollectStats ? $"exceptions{Path.GetExtension(path)}.txt" : "exceptions.txt";

            lock (ConsoleWriterLock)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;

                if (parentPath == null)
                {
                    Console.Error.WriteLine($"File: {path}\n{e}");

                    File.AppendAllText(exceptionsFileName, $"---------------\nFile: {path}\nException: {e}\n\n");
                }
                else
                {
                    Console.Error.WriteLine($"File: {path} (parent: {parentPath})\n{e}");

                    File.AppendAllText(exceptionsFileName, $"---------------\nParent file: {parentPath}\nFile: {path}\nException: {e}\n\n");
                }

                Console.ResetColor();
            }
        }

        private static string FixPathSlashes(string path)
        {
            path = path.Replace('\\', '/');

            if (Path.DirectorySeparatorChar != '/')
            {
                path = path.Replace('/', Path.DirectorySeparatorChar);
            }

            return path;
        }

        private static string GetVersion()
        {
            var info = new StringBuilder();
            info.Append("Version: ");
            info.AppendLine(typeof(Decompiler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion);
            info.Append("OS: ");
            info.AppendLine(RuntimeInformation.OSDescription);
            info.AppendLine("Website: https://valveresourceformat.github.io");
            info.Append("GitHub: https://github.com/ValveResourceFormat/ValveResourceFormat");
            return info.ToString();
        }

        [GeneratedRegex(
            @"(?:_c|\.vcs|\.nav|\.vfe|\.vfont|\.uifont)$|" +
            @"^(?:readonly_)?tools_asset_info\.bin$|" +
            @"^(?:subtitles|closecaption)_.*\.dat$"
        )]
        private static partial Regex SupportedFileNamesRegex();

        [GeneratedRegex(@"_[0-9]{3}\.vpk$")]
        private static partial Regex VpkArchiveIndexRegex();
    }
}

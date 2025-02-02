﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Monitoring.EventPipe;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Options;

namespace Microsoft.Diagnostics.Monitoring.RestServer
{
    internal sealed class DiagnosticServices : IDiagnosticServices
    {
        // The value of the operating system field of the ProcessInfo result when the target process is running
        // on a Windows operating system.
        private const string ProcessOperatingSystemWindowsValue = "windows";

        // A Docker container's entrypoint process ID is 1
        private static readonly ProcessKey DockerEntrypointProcessFilter = new ProcessKey(1);

        // The amount of time to wait when checking if the docker entrypoint process is a .NET process
        // with a diagnostics transport connection.
        private static readonly TimeSpan DockerEntrypointWaitTimeout = TimeSpan.FromMilliseconds(250);
        // The amount of time to wait before cancelling get additional process information (e.g. getting
        // the process command line if the IEndpointInfo doesn't provide it).
        private static readonly TimeSpan ExtendedProcessInfoTimeout = TimeSpan.FromMilliseconds(500);

        private readonly IEndpointInfoSourceInternal _endpointInfoSource;
        private readonly CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private readonly StorageOptions _storageOptions;

        public DiagnosticServices(IEndpointInfoSource endpointInfoSource, IOptions<StorageOptions> storageOptions)
        {
            _endpointInfoSource = (IEndpointInfoSourceInternal)endpointInfoSource;
            _storageOptions = storageOptions.Value;
        }

        public async Task<IEnumerable<IProcessInfo>> GetProcessesAsync(CancellationToken token)
        {
            try
            {
                using CancellationTokenSource extendedInfoCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                IList<Task<ProcessInfo>> processInfoTasks = new List<Task<ProcessInfo>>();
                foreach (IEndpointInfo endpointInfo in await _endpointInfoSource.GetEndpointInfoAsync(token))
                {
                    processInfoTasks.Add(ProcessInfo.FromEndpointInfoAsync(endpointInfo, extendedInfoCancellation.Token));
                }

                // FromEndpointInfoAsync can fill in the command line for .NET Core 3.1 processes by invoking the
                // event pipe and capturing the ProcessInfo event. Timebox this operation with the cancellation token
                // so that getting the process list does not take a long time or wait indefinitely.
                extendedInfoCancellation.CancelAfter(ExtendedProcessInfoTimeout);

                await Task.WhenAll(processInfoTasks);

                return processInfoTasks.Select(t => t.Result);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Unable to enumerate processes.");
            }
        }

        public async Task<Stream> GetDump(IProcessInfo pi, DumpType mode, CancellationToken token)
        {
            string dumpFilePath = Path.Combine(_storageOptions.DumpTempFolder, FormattableString.Invariant($"{Guid.NewGuid()}_{pi.EndpointInfo.ProcessId}"));
            NETCore.Client.DumpType dumpType = MapDumpType(mode);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Get the process
                Process process = Process.GetProcessById(pi.EndpointInfo.ProcessId);
                await Dumper.CollectDumpAsync(process, dumpFilePath, dumpType);
            }
            else
            {
                await Task.Run(() =>
                {
                    var client = new DiagnosticsClient(pi.EndpointInfo.Endpoint);
                    client.WriteDump(dumpType, dumpFilePath);
                });
            }

            return new AutoDeleteFileStream(dumpFilePath);
        }

        private static NETCore.Client.DumpType MapDumpType(DumpType dumpType)
        {
            switch (dumpType)
            {
                case DumpType.Full:
                    return NETCore.Client.DumpType.Full;
                case DumpType.WithHeap:
                    return NETCore.Client.DumpType.WithHeap;
                case DumpType.Triage:
                    return NETCore.Client.DumpType.Triage;
                case DumpType.Mini:
                    return NETCore.Client.DumpType.Normal;
                default:
                    throw new ArgumentException("Unexpected dumpType", nameof(dumpType));
            }
        }

        public async Task<IProcessInfo> GetProcessAsync(ProcessKey? processKey, CancellationToken token)
        {
            var endpointInfos = await _endpointInfoSource.GetEndpointInfoAsync(token);

            if (processKey.HasValue)
            {
                return await GetSingleProcessInfoAsync(
                    endpointInfos,
                    processKey);
            }

            // Short-circuit for when running in a Docker container.
            if (RuntimeInfo.IsInDockerContainer)
            {
                try
                {
                    IProcessInfo processInfo = await GetSingleProcessInfoAsync(
                        endpointInfos,
                        DockerEntrypointProcessFilter);

                    using var timeoutSource = new CancellationTokenSource(DockerEntrypointWaitTimeout);

                    var client = new DiagnosticsClient(processInfo.EndpointInfo.Endpoint);
                    await client.WaitForConnectionAsync(timeoutSource.Token);

                    return processInfo;
                }
                catch
                {
                    // Process ID 1 doesn't exist, didn't advertise in connect mode, or is not a .NET process.
                }
            }

            return await GetSingleProcessInfoAsync(
                endpointInfos,
                processKey: null);
        }

        private async Task<IProcessInfo> GetSingleProcessInfoAsync(IEnumerable<IEndpointInfo> endpointInfos, ProcessKey? processKey)
        {
            if (processKey.HasValue)
            {
                if (processKey.Value.RuntimeInstanceCookie.HasValue)
                {
                    Guid cookie = processKey.Value.RuntimeInstanceCookie.Value;
                    endpointInfos = endpointInfos.Where(info => info.RuntimeInstanceCookie == cookie);
                }

                if (processKey.Value.ProcessId.HasValue)
                {
                    int pid = processKey.Value.ProcessId.Value;
                    endpointInfos = endpointInfos.Where(info => info.ProcessId == pid);
                }
            }

            IEndpointInfo[] endpointInfoArray = endpointInfos.ToArray();
            switch (endpointInfoArray.Length)
            {
                case 0:
                    throw new ArgumentException("Unable to discover a target process.");
                case 1:
                    return await ProcessInfo.FromEndpointInfoAsync(endpointInfoArray[0]);
                default:
#if DEBUG
                    IEndpointInfo endpointInfo = endpointInfoArray.FirstOrDefault(info => string.Equals(Process.GetProcessById(info.ProcessId).ProcessName, "iisexpress", StringComparison.OrdinalIgnoreCase));
                    if (endpointInfo != null)
                    {
                        return await ProcessInfo.FromEndpointInfoAsync(endpointInfo);
                    }
#endif
                    throw new ArgumentException("Unable to select a single target process because multiple target processes have been discovered.");
            }
        }

        public void Dispose()
        {
            _tokenSource.Cancel();
        }

        /// <summary>
        /// We want to make sure we destroy files we finish streaming.
        /// We want to make sure that we stream out files since we compress on the fly; the size cannot be known upfront.
        /// CONSIDER The above implies knowledge of how the file is used by the rest api.
        /// </summary>
        private sealed class AutoDeleteFileStream : FileStream
        {
            public AutoDeleteFileStream(string path) : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.DeleteOnClose)
            {
            }

            public override bool CanSeek => false;
        }


        private sealed class ProcessInfo : IProcessInfo
        {
            // String returned for a process field when its value could not be retrieved. This is the same
            // value that is returned by the runtime when it could not determine the value for each of those fields.
            private const string ProcessFieldUnknownValue = "unknown";

            public ProcessInfo(
                IEndpointInfo endpointInfo,
                string commandLine,
                string processName)
            {
                EndpointInfo = endpointInfo;

                // The GetProcessInfo command will return "unknown" for values for which it does
                // not know the value, such as operating system and process architecture if the
                // process is running on one that is not predefined. Mimic the same behavior here
                // when the extra process information was not provided.
                CommandLine = commandLine ?? ProcessFieldUnknownValue;
                ProcessName = processName ?? ProcessFieldUnknownValue;
            }

            public static async Task<ProcessInfo> FromEndpointInfoAsync(IEndpointInfo endpointInfo)
            {
                using CancellationTokenSource extendedInfoCancellation = new CancellationTokenSource(ExtendedProcessInfoTimeout);
                return await FromEndpointInfoAsync(endpointInfo, extendedInfoCancellation.Token);
            }

            // Creates a ProcessInfo object from the IEndpointInfo. Attempts to get the command line using event pipe
            // if the endpoint information doesn't provide it. The cancelation token can be used to timebox this fallback
            // mechansim.
            public static async Task<ProcessInfo> FromEndpointInfoAsync(IEndpointInfo endpointInfo, CancellationToken extendedInfoCancellationToken)
            {
                if (null == endpointInfo)
                {
                    throw new ArgumentNullException(nameof(endpointInfo));
                }

                var client = new DiagnosticsClient(endpointInfo.Endpoint);

                string commandLine = endpointInfo.CommandLine;
                if (string.IsNullOrEmpty(commandLine))
                {
                    try
                    {
                        var infoSettings = new EventProcessInfoPipelineSettings
                        {
                            Duration = Timeout.InfiniteTimeSpan,
                        };

                        await using var pipeline = new EventProcessInfoPipeline(client, infoSettings,
                            (cmdLine, token) => { commandLine = cmdLine; return Task.CompletedTask; });

                        await pipeline.RunAsync(extendedInfoCancellationToken);
                    }
                    catch
                    {
                    }
                }

                string processName = null;
                if (!string.IsNullOrEmpty(commandLine))
                {
                    // Get the process name from the command line
                    bool isWindowsProcess = false;
                    if (string.IsNullOrEmpty(endpointInfo.OperatingSystem))
                    {
                        // If operating system is null, the process is likely .NET Core 3.1 (which doesn't have the GetProcessInfo command).
                        // Since the underlying diagnostic communication channel used by the .NET runtime requires that the diagnostic process
                        // must be running on the same type of operating system as the target process (e.g. dotnet-monitor must be running on Windows
                        // if the target process is running on Windows), then checking the local operating system should be a sufficient heuristic
                        // to determine the operating system of the target process.
                        isWindowsProcess = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                    }
                    else
                    {
                        isWindowsProcess = ProcessOperatingSystemWindowsValue.Equals(endpointInfo.OperatingSystem, StringComparison.OrdinalIgnoreCase);
                    }

                    string processPath = CommandLineHelper.ExtractExecutablePath(commandLine, isWindowsProcess);
                    if (!string.IsNullOrEmpty(processPath))
                    {
                        processName = Path.GetFileName(processPath);
                        if (isWindowsProcess)
                        {
                            // Remove the extension on Windows to match the behavior of Process.ProcessName
                            processName = Path.GetFileNameWithoutExtension(processName);
                        }
                    }
                }

                return new ProcessInfo(
                    endpointInfo,
                    commandLine,
                    processName);
            }

            public IEndpointInfo EndpointInfo { get; }

            public string CommandLine { get; }

            public string OperatingSystem => EndpointInfo.OperatingSystem ?? ProcessFieldUnknownValue;

            public string ProcessArchitecture => EndpointInfo.ProcessArchitecture ?? ProcessFieldUnknownValue;

            public string ProcessName { get; }
        }
    }
}

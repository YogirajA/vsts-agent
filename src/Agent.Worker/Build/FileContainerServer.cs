﻿using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.FileContainer.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public class FileContainerServer
    {
        private readonly ConcurrentQueue<string> _fileUploadQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentBag<Exception> _exceptionsDuringFileUpload = new ConcurrentBag<Exception>();
        private CancellationTokenSource _uploadCancellationTokenSource;

        private Uri _projectCollectionUrl;
        private VssCredentials _credential;
        private Guid _projectId;
        private long _containerId;
        private string _containerPath;

        private int _filesUploaded = 0;
        private string _sourceParentDirectory;

        private FileContainerHttpClient FileContainerHttpClient { get; }

        public FileContainerServer(
            Uri projectCollectionUrl,
            VssCredentials credential,
            Guid projectId,
            long containerId,
            string containerPath)
        {
            _projectCollectionUrl = projectCollectionUrl;
            _credential = credential;
            _projectId = projectId;
            _containerId = containerId;
            _containerPath = containerPath;

            // default file upload request timeout to 300 seconds
            // TODO: Load from .ini file.
            VssHttpRequestSettings fileUploadRequestSettings = new VssHttpRequestSettings();
            fileUploadRequestSettings.SendTimeout = TimeSpan.FromSeconds(300);
            FileContainerHttpClient = new FileContainerHttpClient(
                _projectCollectionUrl,
                _credential,
                fileUploadRequestSettings,
                new VssHttpRetryMessageHandler(3));
        }

        public async Task CopyToContainerAsync(
            IAsyncCommandContext context,
            String source,
            CancellationToken cancellationToken)
        {
            //set maxConcurrentUploads up to 2 untill figure out how to use WinHttpHandler.MaxConnectionsPerServer modify DefaultConnectionLimit
            int maxConcurrentUploads = Math.Min(Environment.ProcessorCount, 2);
            //context.Output($"Max Concurrent Uploads {maxConcurrentUploads}");

            IEnumerable<String> files;
            if (File.Exists(source))
            {
                files = new List<String>() { source };
                _sourceParentDirectory = Path.GetDirectoryName(source);
            }
            else
            {
                files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories);
                _sourceParentDirectory = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            context.Output(StringUtil.Loc("TotalUploadFiles", files.Count()));
            foreach (var file in files)
            {
                _fileUploadQueue.Enqueue(file);
            }

            using (_uploadCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                List<Task> allRunningTasks = new List<Task>();
                // start reporting task to keep tracking upload progress.
                allRunningTasks.Add(ReportingAsync(context, files.Count(), _uploadCancellationTokenSource.Token));

                // start parallel upload task.
                for (int i = 0; i < maxConcurrentUploads; i++)
                {
                    allRunningTasks.Add(ParallelUploadAsync(context, i, _uploadCancellationTokenSource.Token));
                }

                // the only expected type of exception will throw from both parallel upload task and reporting task is OperationCancelledException.
                try
                {
                    await Task.WhenAll(allRunningTasks);
                }
                catch (OperationCanceledException)
                {
                    // throw aggregate exception for all non-operationcancelexception we catched during file upload.
                    if (_exceptionsDuringFileUpload.Count > 0)
                    {
                        throw new AggregateException(_exceptionsDuringFileUpload).Flatten();
                    }

                    throw;
                }
            }
        }

        private async Task ParallelUploadAsync(IAsyncCommandContext context, int uploaderId, CancellationToken token)
        {
            string fileToUpload;
            while (_fileUploadQueue.TryDequeue(out fileToUpload))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (FileStream fs = File.Open(fileToUpload, FileMode.Open, FileAccess.Read))
                    {
                        string itemPath = (_containerPath.TrimEnd('/') + "/" + fileToUpload.Remove(0, _sourceParentDirectory.Length + 1)).Replace('\\', '/');
                        var response = await FileContainerHttpClient.UploadFileAsync(_containerId, itemPath, fs, _projectId, token);
                        if (response.StatusCode != System.Net.HttpStatusCode.Created)
                        {
                            throw new Exception($"Unable to copy file to server StatusCode={response.StatusCode}: {response.ReasonPhrase}. Source file path: {fileToUpload}. Target server path: {itemPath}");
                        }
                    }

                    Interlocked.Increment(ref _filesUploaded);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _exceptionsDuringFileUpload.Add(ex);
                    _uploadCancellationTokenSource.Cancel();
                    return;
                }
            }
        }

        private async Task ReportingAsync(IAsyncCommandContext context, int totalFiles, CancellationToken token)
        {
            while (_filesUploaded != totalFiles)
            {
                context.Output(StringUtil.Loc("FileUploadProgress", totalFiles, _filesUploaded));
                await Task.Delay(2000, token);
            }
        }
    }
}
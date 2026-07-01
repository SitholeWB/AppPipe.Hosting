using System;
using System.IO;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using AppPipe.Hosting;
using FluentFTP;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using Microsoft.Extensions.Logging;

namespace AppPipe.Hosting;

[DependsOn<WindowsIISSharedDeploymentModule>]
public class FtpUploadModule : Module<string>
{
    private readonly DeploymentOptions _options;

    public FtpUploadModule(DeploymentOptions options)
    {
        _options = options;
    }

    protected override async Task<string?> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var localDir = Path.Combine(Environment.CurrentDirectory, "publish", "shared-hosting");
        var backupDir = Path.Combine(Environment.CurrentDirectory, "publish", "backup");

        if (!Directory.Exists(localDir))
        {
            context.Logger.LogError($"Local directory '{localDir}' does not exist. Cannot upload via FTP.");
            return "Local directory not found";
        }

        context.Logger.LogInformation($"Connecting to FTP server: {_options.FtpHost}:{_options.FtpPort} (SSL: {_options.FtpUseSsl})...");

        // Initialize Async FTP Client
        using (var client = new AsyncFtpClient(_options.FtpHost, _options.FtpUsername, _options.FtpPassword, _options.FtpPort))
        {
            if (_options.FtpUseSsl)
            {
                client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
                client.Config.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                // Accept self-signed certs commonly used in custom server setups
                client.ValidateCertificate += (control, e) => e.Accept = true;
            }

            await client.AutoConnect(cancellationToken);
            context.Logger.LogInformation("Connected successfully to FTP server.");

            var remotePath = _options.FtpRemotePath;
            if (!remotePath.StartsWith("/")) remotePath = "/" + remotePath;

            var backupPathCreated = false;

            // 1. Perform Backup if Enabled
            if (_options.BackupBeforeDeploy)
            {
                context.Logger.LogInformation($"Backup before deploy is enabled. Downloading existing remote files from '{remotePath}' to '{backupDir}'...");
                
                if (Directory.Exists(backupDir))
                {
                    try { Directory.Delete(backupDir, true); } catch { }
                }
                Directory.CreateDirectory(backupDir);

                try
                {
                    if (await client.DirectoryExists(remotePath, cancellationToken))
                    {
                        await client.DownloadDirectory(
                            backupDir, 
                            remotePath, 
                            FtpFolderSyncMode.Update, 
                            FtpLocalExists.Overwrite,
                            FtpVerify.None,
                            null,
                            new Progress<FtpProgress>(progress => {
                                if (progress.Progress > 0)
                                {
                                    context.Logger.LogInformation($"FTP Backup Downloading: {progress.Progress:F1}% completed.");
                                }
                            }),
                            cancellationToken);
                        
                        backupPathCreated = true;
                        context.Logger.LogInformation("Backup completed successfully!");
                    }
                    else
                    {
                        context.Logger.LogInformation("Remote target path does not exist yet. Skipping backup.");
                    }
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning($"Warning: Backup failed: {ex.Message}. Deployment will proceed, but rollback will not be available.");
                }
            }

            // 2. Perform Sync Upload
            try
            {
                context.Logger.LogInformation($"Uploading files recursively to remote directory: {remotePath}...");

                // Sync files recursively
                await client.UploadDirectory(
                    localDir, 
                    remotePath, 
                    FtpFolderSyncMode.Update, 
                    FtpRemoteExists.Overwrite,
                    FtpVerify.None,
                    null,
                    new Progress<FtpProgress>(progress => {
                        if (progress.Progress > 0)
                        {
                            context.Logger.LogInformation($"FTP Uploading: {progress.Progress:F1}% completed.");
                        }
                    }),
                    cancellationToken);

                context.Logger.LogInformation("FTP directory sync completed successfully!");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"FTP Upload failed: {ex.Message}");

                // 3. Rollback on Error
                if (_options.BackupBeforeDeploy && _options.RollbackOnError && backupPathCreated && Directory.Exists(backupDir))
                {
                    context.Logger.LogWarning("Rollback is enabled. Starting rollback to restore previous server state...");
                    try
                    {
                        if (!client.IsConnected)
                        {
                            await client.AutoConnect(cancellationToken);
                        }

                        // Mirror restores backup exactly (it uploads backup and deletes any corrupted/extraneous new files)
                        await client.UploadDirectory(
                            backupDir, 
                            remotePath, 
                            FtpFolderSyncMode.Mirror, 
                            FtpRemoteExists.Overwrite,
                            FtpVerify.None,
                            null,
                            new Progress<FtpProgress>(progress => {
                                if (progress.Progress > 0)
                                {
                                    context.Logger.LogInformation($"FTP Rollback Restoring: {progress.Progress:F1}% completed.");
                                }
                            }),
                            cancellationToken);
                        
                        context.Logger.LogInformation("Rollback completed successfully! Previous state has been restored.");
                    }
                    catch (Exception rollbackEx)
                    {
                        context.Logger.LogCritical($"CRITICAL ERROR: Rollback failed! Server may be in an inconsistent state: {rollbackEx.Message}");
                    }
                }

                throw; // Re-throw the original exception to mark the module as failed
            }

            await client.Disconnect(cancellationToken);
        }

        return "FTP Upload Succeeded";
    }
}

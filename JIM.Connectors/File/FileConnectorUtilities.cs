using System.Diagnostics;

namespace JIM.Connectors.File
{
    public class Utilities
    {
        public static bool MountFileShare()
        {
            var wasSuccessful = false;

            try
            {
                var mountPath = "<localpath>"; // i.e. /LocalFolderName
                var sharePath = "//<host>/<path>";
                var username = "<username>";
                var password = "<password>";
                var mkdirArgs = $"-p \"{mountPath}\"";
                var mountArgs = $"-t cifs -o username={username},password={password} {sharePath} {mountPath}";
                var message = string.Empty;

                if (ExecuteOperatingSystemCommand("mkdir", mkdirArgs, out message))
                {
                    //Logger.LogInformation($"Output 1: {message}");

                    if (ExecuteOperatingSystemCommand("mount", mountArgs, out message))
                    {
                        //Logger.LogInformation($"Output 2: {message}");

                        string connectingTestingFile = $"{Guid.NewGuid()}.txt";
                        string filePath = Path.Combine(mountPath, connectingTestingFile);

                        //Logger.LogInformation("Testing file path: " + filePath);

                        System.IO.File.Create(filePath);
                        if (System.IO.File.Exists(filePath))
                        {
                            System.IO.File.Delete(filePath);
                            wasSuccessful = true;
                        }

                        if (wasSuccessful)
                        {
                            //Logger.LogInformation("Network drive mounted successfully");
                        }
                        else
                        {
                            //Logger.LogError("Network drive mounting failed");
                        }
                    }
                    else
                    {
                        //Logger.LogError($"Error Output 2: {message}");
                    }
                }
                else
                {
                    //Logger.LogError($"Error Output 2: {message}");
                }
            }
            catch (Exception ex)
            {
                //Logger.LogError(ex, $"Error message - {ex.Message}");
            }

            return wasSuccessful;
        }

        public static bool ExecuteOperatingSystemCommand(string command, string args, out string message)
        {
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (string.IsNullOrEmpty(error))
            {
                message = output;
                return true;
            }
            else
            {
                message = error;
                return true;
            }
        }
    }
}

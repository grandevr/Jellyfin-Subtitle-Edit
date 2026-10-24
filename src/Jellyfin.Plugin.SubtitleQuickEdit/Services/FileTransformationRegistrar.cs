using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Services;

/// <summary>
/// Registers <see cref="IndexHtmlInjector"/> with the File Transformation plugin when Jellyfin starts. That plugin
/// lives in its own load context and cannot be referenced, so this goes by reflection. If it is missing or does not
/// work, the plugin logs one clear error and the server API carries on.
/// </summary>
public sealed class FileTransformationRegistrar : IHostedService, IDisposable
{
    /// <summary>
    /// The file this plugin transforms. File Transformation matches it against the path after <c>/web/</c>, first as an
    /// exact string and otherwise as a regular expression; the plain name keeps this transformation in the same
    /// pipeline as other plugins that register <c>index.html</c> the same way.
    /// </summary>
    public const string FileNamePattern = "index.html";

    /// <summary>
    /// The id of this plugin's transformation. File Transformation ignores a second registration with the same id.
    /// </summary>
    public static readonly Guid TransformationId = new("6f1c7a52-3a0e-4d0a-9d54-8b1f6e5c2a10");

    private const string AssemblyMarker = ".FileTransformation";
    private const string InterfaceType = "Jellyfin.Plugin.FileTransformation.PluginInterface";
    private const string RegisterMethod = "RegisterTransformation";

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    };

    private readonly ILogger<FileTransformationRegistrar> logger;
    private readonly CancellationTokenSource stopping = new();
    private Task? work;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationRegistrar"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public FileTransformationRegistrar(ILogger<FileTransformationRegistrar> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Registering must not hold up Jellyfin's startup, so it runs on its own.
        work = Task.Run(() => RegisterWithRetriesAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        if (work is not null)
        {
            await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => stopping.Dispose();

    private static string DescribeFailure(Exception ex)
    {
        Exception inner = ex is TargetInvocationException { InnerException: not null } wrapped ? wrapped.InnerException! : ex;
        return inner.GetType().Name + ": " + inner.Message;
    }

    private static string BuildPayloadJson()
    {
        Type callback = typeof(IndexHtmlInjector);
        return JsonSerializer.Serialize(new
        {
            id = TransformationId,
            fileNamePattern = FileNamePattern,
            callbackAssembly = callback.Assembly.FullName,
            callbackClass = callback.FullName,
            callbackMethod = nameof(IndexHtmlInjector.Inject),
        });
    }

    private async Task RegisterWithRetriesAsync(CancellationToken cancellationToken)
    {
        string lastFailure = string.Empty;
        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(RetryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            Assembly? fileTransformation = FindFileTransformation();
            if (fileTransformation is null)
            {
                logger.LogError(
                    "Subtitle Quick Edit: the File Transformation plugin (3.0.2.0 or later) is not installed or not enabled, so the edit button cannot be added to the web page. The server API still works. Install or enable File Transformation and restart Jellyfin.");
                return;
            }

            try
            {
                Register(fileTransformation);
                logger.LogInformation(
                    "Subtitle Quick Edit: registered the client script with File Transformation {Version} (id {Id}, file {Pattern}, attempt {Attempt}).",
                    fileTransformation.GetName().Version,
                    TransformationId,
                    FileNamePattern,
                    attempt + 1);
                return;
            }
            catch (MissingMemberException ex)
            {
                logger.LogError(
                    "Subtitle Quick Edit: File Transformation {Version} does not offer {Type}.{Method} ({Message}). 3.0.2.0 or later is needed, so the edit button cannot be added to the web page. The server API still works.",
                    fileTransformation.GetName().Version,
                    InterfaceType,
                    RegisterMethod,
                    ex.Message);
                return;
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException)
            {
                // Most likely File Transformation has loaded but is not ready yet; try again shortly.
                lastFailure = DescribeFailure(ex);
            }
        }

        logger.LogError(
            "Subtitle Quick Edit: could not register with File Transformation after {Attempts} attempts (last error: {Error}). The edit button will not appear on the web page. The server API still works. Restart Jellyfin to try again.",
            RetryDelays.Length + 1,
            lastFailure);
    }

    private Assembly? FindFileTransformation() =>
        AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(assembly => assembly.FullName?.Contains(AssemblyMarker, StringComparison.Ordinal) == true);

    private void Register(Assembly fileTransformation)
    {
        Type pluginInterface = fileTransformation.GetType(InterfaceType, throwOnError: false)
            ?? throw new MissingMemberException(InterfaceType);
        MethodInfo register = pluginInterface.GetMethod(RegisterMethod, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMemberException(InterfaceType, RegisterMethod);
        ParameterInfo[] parameters = register.GetParameters();
        if (parameters.Length != 1)
        {
            throw new MissingMemberException(InterfaceType, RegisterMethod);
        }

        // The argument is a Newtonsoft JObject from File Transformation's own copy of Newtonsoft.Json, so build it
        // through that type rather than referencing the library here.
        MethodInfo parse = parameters[0].ParameterType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) })
            ?? throw new MissingMemberException(parameters[0].ParameterType.FullName, "Parse");
        object? payload = parse.Invoke(null, new object[] { BuildPayloadJson() });
        register.Invoke(null, new[] { payload });
    }
}

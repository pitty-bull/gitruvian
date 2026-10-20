using System.Globalization;

namespace Gitruvian.Core;

/// <summary>
/// Log técnico local. Registra apenas metadados (ids, contagens, códigos de saída). Nunca passe conteúdo de
/// arquivos, diffs, prompts ou respostas do modelo. Como defesa extra, todo registro passa por redação de
/// segredos e é truncado.
/// </summary>
public static class Log
{
    public const int MaxMessageChars = 300;
    public const long MaxBytes = 512_000;
    public const int Backups = 2;
    private static readonly Lock Gate = new();
    private static string? _path;

    public static string? CurrentPath
    {
        get
        {
            lock (Gate) return _path;
        }
    }

    /// <summary>Passa a gravar em &lt;pasta de dados&gt;/gitruvian.log.</summary>
    public static void Setup(string dataDir)
    {
        lock (Gate) _path = Path.Combine(dataDir, "gitruvian.log");
    }

    /// <summary>Para de gravar (necessário no Windows antes de apagar a pasta de dados).</summary>
    public static void Close()
    {
        lock (Gate) _path = null;
    }

    public static string Sanitize(string message) =>
        Safety.Truncate(Safety.Redact(message.Replace("\r", " ").Replace("\n", " ")), MaxMessageChars);

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARNING", message);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_path is null)
                return;
            try
            {
                Rotate(_path);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture);
                // Abre e fecha a cada registro: nenhum arquivo fica preso aberto.
                File.AppendAllText(_path, $"{stamp} {level} {Sanitize(message)}\n");
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                // Log é auxiliar: falha ao gravar nunca interrompe a operação.
            }
        }
    }

    private static void Rotate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes)
            return;
        for (var i = Backups; i >= 1; i--)
        {
            var source = i == 1 ? path : $"{path}.{i - 1}";
            if (File.Exists(source))
                File.Move(source, $"{path}.{i}", overwrite: true);
        }
    }
}

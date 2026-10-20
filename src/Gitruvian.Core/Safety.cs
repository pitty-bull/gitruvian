using System.Text;
using System.Text.RegularExpressions;

namespace Gitruvian.Core;

/// <summary>Caminho rejeitado por sair do repositório ou tocar áreas protegidas.</summary>
public sealed class UnsafePathException(string message) : ArgumentException(message);

/// <summary>Regras de segurança: caminhos sensíveis, redação de segredos e validação de entradas.</summary>
public static partial class Safety
{
    /// <summary>Arquivos que nunca são indexados, enviados ao modelo ou alterados por um diff.</summary>
    public static readonly string[] SensitivePatterns =
    [
        ".env", ".env.*", "*.env", "*.pem", "*.key", "*.p12", "*.pfx", "*.jks", "*.keystore", "*.kdbx",
        "*.tfstate", "*.tfstate.*", "id_rsa*", "id_dsa*", "id_ecdsa*", "id_ed25519*", ".netrc", ".npmrc",
        ".pypirc", ".git-credentials", "credentials", "credentials.*", "*secret*", "*.sqlite", "*.db",
    ];

    /// <summary>Modelos de configuração sem segredos reais podem ser indexados.</summary>
    public static readonly HashSet<string> SafeTemplateNames =
        [".env.example", ".env.sample", ".env.template", ".env.dist"];

    /// <summary>Diretórios internos que um diff nunca pode tocar.</summary>
    public static readonly string[] ProtectedDirs = [".git", ".gitruvian"];

    public const string Redacted = "[REDIGIDO]";
    public const int MaxInstructionChars = 4000;

    private static readonly Regex[] SecretRes =
    [
        new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.Singleline),
        new(@"sk-ant-[A-Za-z0-9_\-]{10,}"),
        new(@"\bsk-[A-Za-z0-9_\-]{20,}"),
        new(@"\bAKIA[0-9A-Z]{16}\b"),
        new(@"\bgh[pousr]_[A-Za-z0-9]{30,}\b"),
        new(@"\bgithub_pat_[A-Za-z0-9_]{20,}\b"),
        new(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}"),
        new(@"\bAIza[0-9A-Za-z\-_]{35}\b"),
        new(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}"),
    ];

    [GeneratedRegex(@"\b([A-Za-z0-9_]*(?:api[_-]?key|secret|passw(?:or)?d|token|access[_-]?key|private[_-]?key)[A-Za-z0-9_]*)(\s*[:=]\s*)(['""]?)([^\s'""]{6,})(['""]?)", RegexOptions.IgnoreCase)]
    private static partial Regex AssignmentRe();

    [GeneratedRegex(@"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")]
    internal static partial Regex ControlRe();

    [GeneratedRegex(@"^[A-Za-z]:")]
    private static partial Regex DriveRe();

    /// <summary>Substitui padrões que parecem segredos por um marcador.</summary>
    public static string Redact(string text)
    {
        foreach (var pattern in SecretRes)
            text = pattern.Replace(text, Redacted);
        return AssignmentRe().Replace(text, m =>
            m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + Redacted + m.Groups[5].Value);
    }

    private static string[] Parts(string path) =>
        path.Replace('\\', '/').Split('/').Where(p => p.Length > 0 && p != ".").ToArray();

    /// <summary>True se o caminho (relativo ao repositório) parece conter segredos.</summary>
    public static bool IsSensitivePath(string path)
    {
        var parts = Parts(path);
        if (parts.Length == 0)
            return false;
        var name = parts[^1].ToLowerInvariant();
        if (SafeTemplateNames.Contains(name))
            return false;
        if (parts[..^1].Any(p => p.ToLowerInvariant() is ".ssh" or ".gnupg" or ".aws"))
            return true;
        return SensitivePatterns.Any(pattern => Glob.Match(name, pattern));
    }

    /// <summary>
    /// Valida e normaliza um caminho relativo vindo de fonte não confiável. Retorna o caminho com '/'.
    /// Rejeita caminhos absolutos, com '..', em diretórios internos ou sensíveis (a menos que
    /// <paramref name="allowSensitive"/> seja true, usado em stage e descarte feitos pelo próprio usuário).
    /// </summary>
    public static string CheckRepoRelativePath(string path, bool allowSensitive = false)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024)
            throw new UnsafePathException("caminho vazio ou longo demais");
        if (path.Contains('\0') || ControlRe().IsMatch(path))
            throw new UnsafePathException("caminho contém caracteres de controle");
        if (path.Contains('\\'))
            throw new UnsafePathException($"use '/' como separador: '{path}'");
        if (path.StartsWith('/') || DriveRe().IsMatch(path))
            throw new UnsafePathException($"caminho absoluto não é permitido: '{path}'");
        var parts = Parts(path);
        if (parts.Contains(".."))
            throw new UnsafePathException($"caminho sai do repositório: '{path}'");
        var normalized = string.Join('/', parts);
        if (normalized.Length == 0)
            throw new UnsafePathException("caminho vazio");
        if (ProtectedDirs.Contains(parts[0].ToLowerInvariant()))
            throw new UnsafePathException($"diretório protegido: '{normalized}'");
        if (!allowSensitive && IsSensitivePath(normalized))
            throw new UnsafePathException($"arquivo sensível (possível segredo): '{normalized}'");
        return normalized;
    }

    /// <summary>Normaliza e valida o pedido do usuário.</summary>
    public static string CleanInstruction(string? text)
    {
        var value = (text ?? "").Normalize(NormalizationForm.FormC);
        value = ControlRe().Replace(value, "").Replace("\r\n", "\n").Trim();
        if (value.Length < 3)
            throw new ArgumentException("descreva a alteração com pelo menos 3 caracteres");
        if (value.Length > MaxInstructionChars)
            throw new ArgumentException($"pedido longo demais (máximo {MaxInstructionChars} caracteres)");
        return value;
    }

    public static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..Math.Max(0, limit - 1)] + "…";
}

/// <summary>Correspondência de padrões no estilo fnmatch (*, ?, [seq], [!seq]), sensível a maiúsculas.</summary>
public static class Glob
{
    private static readonly Dictionary<string, Regex> Cache = new();
    private static readonly Lock CacheLock = new();

    public static bool Match(string name, string pattern)
    {
        Regex regex;
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(pattern, out regex!))
            {
                regex = new Regex(Translate(pattern), RegexOptions.Singleline | RegexOptions.CultureInvariant);
                Cache[pattern] = regex;
            }
        }
        return regex.IsMatch(name);
    }

    private static string Translate(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                case '[':
                    var j = i + 1;
                    if (j < pattern.Length && pattern[j] == '!') j++;
                    if (j < pattern.Length && pattern[j] == ']') j++;
                    while (j < pattern.Length && pattern[j] != ']') j++;
                    if (j >= pattern.Length)
                    {
                        sb.Append(@"\[");
                    }
                    else
                    {
                        var body = pattern[(i + 1)..j].Replace(@"\", @"\\");
                        i = j;
                        if (body.StartsWith('!')) body = "^" + body[1..];
                        else if (body.StartsWith('^')) body = @"\" + body;
                        sb.Append('[').Append(body).Append(']');
                    }
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.Append('$').ToString();
    }
}

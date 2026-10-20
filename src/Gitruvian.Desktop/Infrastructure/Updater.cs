using Gitruvian.Core;
using Velopack;
using Velopack.Sources;

namespace Gitruvian.Desktop.Infrastructure;

public enum UpdateState
{
    NotInstalled,
    UpToDate,
    Available,
    Downloaded,
}

public sealed record UpdateCheck(UpdateState State, string CurrentVersion, string? NewVersion, string Message);

/// <summary>Atualização automática. A interface usa esta abstração; os testes usam uma versão falsa.</summary>
public interface IUpdater
{
    /// <summary>De onde as atualizações vêm (mostrado ao usuário antes de ativar).</summary>
    string SourceDescription { get; }

    /// <summary>O app foi instalado pelo Setup (só assim ele pode se atualizar).</summary>
    bool IsInstalled { get; }

    UpdateCheck Check();
    UpdateCheck Download(Action<int>? progress = null);
    void ApplyAndRestart();

    /// <summary>Se há atualização baixada, aplica quando o app fechar (sem reiniciar).</summary>
    void ApplyOnExit();
}

/// <summary>
/// Atualizações via Velopack. Só consulta a rede quando chamado (o app chama apenas com o consentimento do
/// usuário ou pelo menu Ajuda › Verificar atualizações). Não envia dados do usuário nem dos repositórios.
/// </summary>
public sealed class VelopackUpdater : IUpdater
{
    public const string GithubRepo = "https://github.com/pitty-bull/gitruvian";
    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;
    private bool _downloaded;

    public VelopackUpdater(Settings settings)
    {
        if (settings.UpdateUrl.Length > 0)
        {
            _manager = new UpdateManager(settings.UpdateUrl);
            SourceDescription = settings.UpdateUrl;
        }
        else
        {
            _manager = new UpdateManager(new GithubSource(GithubRepo, null, false));
            SourceDescription = GithubRepo + "/releases";
        }
    }

    public string SourceDescription { get; }

    public bool IsInstalled => _manager.IsInstalled;

    private string Current => _manager.CurrentVersion?.ToString() ?? Transfer.AppVersion;

    public UpdateCheck Check()
    {
        if (!_manager.IsInstalled)
            return new UpdateCheck(UpdateState.NotInstalled, Transfer.AppVersion, null,
                "A atualização automática só funciona na versão instalada pelo Setup do Gitruvian.");
        if (_downloaded && _pending is not null)
            return new UpdateCheck(UpdateState.Downloaded, Current, _pending.TargetFullRelease.Version.ToString(),
                "Atualização baixada. Reinicie o Gitruvian para concluir.");
        _pending = _manager.CheckForUpdates();
        if (_pending is null)
            return new UpdateCheck(UpdateState.UpToDate, Current, null, $"Você está na versão mais recente ({Current}).");
        var version = _pending.TargetFullRelease.Version.ToString();
        return new UpdateCheck(UpdateState.Available, Current, version, $"Versão {version} disponível (atual: {Current}).");
    }

    public UpdateCheck Download(Action<int>? progress = null)
    {
        if (_pending is null)
        {
            var check = Check();
            if (check.State != UpdateState.Available)
                return check;
        }
        _manager.DownloadUpdates(_pending!, progress);
        _downloaded = true;
        var version = _pending!.TargetFullRelease.Version.ToString();
        Log.Info($"update_downloaded version={version}");
        return new UpdateCheck(UpdateState.Downloaded, Current, version,
            $"Versão {version} baixada. Reinicie para concluir a atualização.");
    }

    public void ApplyAndRestart()
    {
        if (_pending is null || !_downloaded)
            throw new InvalidOperationException("nenhuma atualização baixada");
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    public void ApplyOnExit()
    {
        if (_pending is not null && _downloaded)
            _manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: false);
    }
}

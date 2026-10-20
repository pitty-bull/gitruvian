using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Gitruvian.Desktop.Infrastructure;

/// <summary>
/// Executa tarefas fora da thread da interface, uma de cada vez (evita disputa pelo index.lock do git e
/// pelo banco). Os callbacks voltam na thread da interface. Tarefas "quiet" (leitura de diff, detalhes)
/// não deixam a interface ocupada.
/// </summary>
public sealed partial class TaskRunner : ObservableObject
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pending;
    private int _busyCount;

    /// <summary>Há uma tarefa que altera o repositório em andamento (botões ficam desabilitados).</summary>
    [ObservableProperty]
    private bool _busy;

    /// <summary>Descrição da tarefa em andamento, para a barra de status.</summary>
    [ObservableProperty]
    private string _busyLabel = "";

    /// <summary>Tarefas enviadas e ainda não concluídas (incluindo os callbacks).</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Submit<T>(string label, Func<T> work, Action<T> onDone, Action<Exception> onError, bool quiet = false)
    {
        Interlocked.Increment(ref _pending);
        if (!quiet)
        {
            _busyCount++;
            BusyLabel = label;
            Busy = true;
        }
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            T result = default!;
            Exception? error = null;
            try
            {
                result = work();
            }
            catch (Exception exc)
            {
                error = exc;
            }
            finally
            {
                _gate.Release();
            }
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (!quiet && --_busyCount == 0)
                    {
                        Busy = false;
                        BusyLabel = "";
                    }
                    if (error is null) onDone(result);
                    else onError(error);
                }
                finally
                {
                    // Decrementa depois do callback: tarefas encadeadas já foram contadas.
                    Interlocked.Decrement(ref _pending);
                }
            });
        });
    }

    public void Submit(string label, Action work, Action onDone, Action<Exception> onError, bool quiet = false) =>
        Submit(label, () =>
        {
            work();
            return true;
        }, _ => onDone(), onError, quiet);
}

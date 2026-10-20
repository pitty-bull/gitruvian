using Gitruvian.Core;

namespace Gitruvian.Desktop.Infrastructure;

/// <summary>Converte exceções em (mensagem, como resolver) para o usuário. Nunca mostra stack trace.</summary>
public static class ErrorText
{
    public static (string Message, string Hint) Friendly(Exception exc)
    {
        if (exc is AggregateException { InnerExceptions.Count: 1 } agg)
            exc = agg.InnerExceptions[0];
        var message = exc.Message.Trim();
        if (message.Length == 0) message = exc.GetType().Name;
        switch (exc)
        {
            case ModelUnavailableException:
                return (message, "Configure um modelo no .env (veja Ajuda › Modelo e modo manual) ou use o diff manual.");
            case ModelException:
                return (message, "Verifique se o modelo está rodando e tente de novo, ou use o diff manual.");
            case DiffValidationException:
                return (message, "Revise o diff: ele precisa estar no formato unificado do git e tocar só arquivos permitidos.");
            case WorkflowException or InvalidTransitionException:
                return (message, "");
            case GitException:
                return (message, "Confira o estado do repositório (arquivos em conflito, branch, permissões).");
            case ImportValidationException or ConfigException or SampleException or UnsafePathException
                or NotFoundException or AmbiguousIdException or NotInitializedException or ArgumentException:
                return (message, "");
            case IOException or UnauthorizedAccessException:
                return (message, "Confira se o arquivo não está aberto em outro programa e se você tem permissão.");
            default:
                Log.Warning($"unexpected_error type={exc.GetType().Name}");
                return ($"erro inesperado ({exc.GetType().Name})", "Tente de novo; se persistir, veja o arquivo gitruvian.log.");
        }
    }
}

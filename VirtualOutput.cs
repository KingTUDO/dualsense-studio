using System;
using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360.Exceptions;

namespace DualSenseStudio;

internal readonly record struct Xbox360State(
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY);

internal sealed class VirtualOutput : IDisposable
{
    private readonly object _sync = new();
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _controller;
    private Xbox360State _lastSubmitted;
    private bool _disposed;

    public VirtualOutput()
    {
        ViGEmClient? client = null;
        IXbox360Controller? controller = null;

        try
        {
            client = new ViGEmClient();
            controller = client.CreateXbox360Controller();
            controller.AutoSubmitReport = false;
            controller.Connect();
            controller.ResetReport();
            controller.SubmitReport();
        }
        catch (Exception exception)
        {
            if (controller is not null)
            {
                TryCleanup(controller.Disconnect);
                TryCleanup(((IDisposable)controller).Dispose);
            }

            if (client is not null)
            {
                TryCleanup(client.Dispose);
            }

            throw new InvalidOperationException(DescribeStartupFailure(exception), exception);
        }

        _client = client;
        _controller = controller;
    }

    public int? UserIndex
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                try
                {
                    return _controller.UserIndex;
                }
                catch (Xbox360UserIndexNotReportedException)
                {
                    return null;
                }
            }
        }
    }

    public bool Submit(in Xbox360State state)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (state == _lastSubmitted) return false;

            _controller.ButtonState = state.Buttons;
            _controller.LeftTrigger = state.LeftTrigger;
            _controller.RightTrigger = state.RightTrigger;
            _controller.LeftThumbX = state.LeftX;
            _controller.LeftThumbY = state.LeftY;
            _controller.RightThumbX = state.RightX;
            _controller.RightThumbY = state.RightY;
            _controller.SubmitReport();
            _lastSubmitted = state;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TryCleanup(() =>
            {
                _controller.ResetReport();
                _controller.SubmitReport();
            });
            TryCleanup(_controller.Disconnect);
            TryCleanup(((IDisposable)_controller).Dispose);
            TryCleanup(_client.Dispose);
        }
    }

    private static string DescribeStartupFailure(Exception exception) => exception switch
    {
        VigemBusNotFoundException => "O driver ViGEmBus não está disponível. Instale o ViGEmBus antes de ativar a saída virtual.",
        VigemBusVersionMismatchException => "A versão instalada do ViGEmBus é incompatível com a saída virtual.",
        VigemBusAccessFailedException => "O Windows não permitiu o acesso ao driver ViGEmBus.",
        VigemNoFreeSlotException => "O ViGEmBus está sem slots livres. Desconecte outra saída virtual e tente novamente.",
        DllNotFoundException or BadImageFormatException or EntryPointNotFoundException =>
            "O componente nativo do ViGEm está ausente ou é incompatível. Use a versão x64 completa do aplicativo.",
        _ => $"Não foi possível criar a saída Xbox 360 virtual. {exception.Message}"
    };

    private static void TryCleanup(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }
}

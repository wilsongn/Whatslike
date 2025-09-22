using System;
using System.Windows.Input;

namespace Chat.Client.Wpf.Infrastructure
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _exec;
        private readonly Func<object?, bool>? _can;

        public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null)
        {
            _exec = exec;
            _can = can;
        }

        public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;

        // Integra com o CommandManager para reconsultar CanExecute automaticamente
        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        // Força reconsulta global (útil quando você sabe que algo mudou)
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

        public void Execute(object? parameter) => _exec(parameter);
    }
}

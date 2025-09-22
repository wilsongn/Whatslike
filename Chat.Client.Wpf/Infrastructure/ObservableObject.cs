// Infrastructure/ObservableObject.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Chat.Client.Wpf.Infrastructure
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(storage, value)) return false;
            storage = value;
            RaisePropertyChanged(name);
            return true;
        }
    }
}

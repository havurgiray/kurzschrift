using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Kurzschrift;

public abstract class Observable : INotifyPropertyChanged {
  public event PropertyChangedEventHandler? PropertyChanged;

  protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
    if (EqualityComparer<T>.Default.Equals(field, value)) return false;
    field = value;
    Raise(name);
    return true;
  }

  protected void Raise([CallerMemberName] string? name = null) =>
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// The UI thread: all session state lives there, like the Mac version's @MainActor.
public static class Ui {
  public static void Post(Action action) {
    var d = Application.Current?.Dispatcher;
    if (d == null || d.CheckAccess()) action(); else d.BeginInvoke(action);
  }

  public static Task Run(Func<Task> action) {
    var d = Application.Current?.Dispatcher;
    if (d == null || d.CheckAccess()) return action();
    return d.InvokeAsync(action).Task.Unwrap();
  }
}

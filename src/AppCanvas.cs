using System.Diagnostics;
using DrawnUi.Views;

namespace CameraTests.UI;

/// <summary>

/// </summary>
public class AppCanvas : Canvas
{

    #region XAML HotReload

    // We need this to handle XAML hot reload propely.
    // It would just re-create a new instance of AppCanvas without disconnecting handler on the old one.
    // So we need some hacks to assure be behave like a singleton.


    public static event EventHandler WasReloaded;

    static AppCanvas _instance;
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (Handler!=null)
        {
            if (_instance != null && _instance != this)
            {
                //XAML HotReload hit, cleanup its leaks
                _instance.DisconnectHandlers();
                _instance.Dispose();
                WasReloaded?.Invoke(this, EventArgs.Empty);
            }

            _instance = this;
            Debug.WriteLine($"[CANVAS] activated {this.Uid}");
        }
        else
        {
            //this will never be hit with hotreload unfortunately
        }
    }

    #endregion
         


}
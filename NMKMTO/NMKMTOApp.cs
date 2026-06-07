using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RibbonPanel = Autodesk.Revit.UI.RibbonPanel;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using TaskDialogCommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons;
namespace NMKMTOTools
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Threading;
  using System.Threading.Tasks;
  using System.Windows;
  using System.Windows.Media;
  using System.Windows.Media.Imaging;
  using System.Windows.Threading;

  public static class PushButtonGifDecoder
  {
    // Tr? v? list các frame dã composited hoàn ch?nh (dã Freeze) kèm delay (ms),
    // và dã scale v? targetSize x targetSize (square).
    // Decode và compositing th?c hi?n trên m?t thread STA riêng.
    public static Task<List<(BitmapSource Frame, int DelayMs)>> DecodeGifFramesScaledAsync(string gifPath, int targetSize, CancellationToken token)
    {
      var tcs = new TaskCompletionSource<List<(BitmapSource, int)>>();

      Thread sta = new Thread(() =>
      {
        try
        {
          if (token.IsCancellationRequested)
          {
            tcs.SetCanceled();
            return;
          }

          List<(BitmapSource, int)> result = DecodeAndScale(gifPath, targetSize);
          tcs.SetResult(result);
        }
        catch (OperationCanceledException)
        {
          tcs.SetCanceled();
        }
        catch (Exception ex)
        {
          tcs.SetException(ex);
        }
        finally
        {
          // Stop the dispatcher for this STA thread if running
          try
          {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
          }
          catch { }
        }
      });

      sta.SetApartmentState(ApartmentState.STA);
      sta.IsBackground = true;
      sta.Start();

      return tcs.Task;
    }

    // N?i b?: ch?y trên STA thread
    private static List<(BitmapSource, int)> DecodeAndScale(string gifPath, int targetSize)
    {
      var list = new List<(BitmapSource, int)>();

      using (var fs = File.OpenRead(gifPath))
      {
        var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
          return list;

        int canvasW = decoder.Frames[0].PixelWidth;
        int canvasH = decoder.Frames[0].PixelHeight;

        // init current composite as transparent
        RenderTargetBitmap currentComposite = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
        {
          var dvInit = new DrawingVisual();
          using (var dc = dvInit.RenderOpen())
          {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, canvasW, canvasH));
          }
          currentComposite.Render(dvInit);
        }

        BitmapSource backupComposite = null;

        for (int i = 0; i < decoder.Frames.Count; ++i)
        {
          var frame = decoder.Frames[i];
          var meta = frame.Metadata as BitmapMetadata;

          // delay (1/100 sec)
          int delayMs = 100;
          try
          {
            object q = meta?.GetQuery("/grctlext/Delay");
            if (q != null)
            {
              ushort d = (ushort)q;
              delayMs = Math.Max(10, d * 10);
            }
          }
          catch { }

          // disposal
          byte disposal = 0;
          try
          {
            object q = meta?.GetQuery("/grctlext/Disposal");
            if (q != null)
              disposal = (byte)q;
          }
          catch { }

          // frame position and size
          int left = 0, top = 0, fw = frame.PixelWidth, fh = frame.PixelHeight;
          try
          {
            object qL = meta?.GetQuery("/imgdesc/Left");
            object qT = meta?.GetQuery("/imgdesc/Top");
            object qW = meta?.GetQuery("/imgdesc/Width");
            object qH = meta?.GetQuery("/imgdesc/Height");
            if (qL != null)
              left = (int)(ushort)qL;
            if (qT != null)
              top = (int)(ushort)qT;
            if (qW != null)
              fw = (int)(ushort)qW;
            if (qH != null)
              fh = (int)(ushort)qH;
          }
          catch { /* ignore */ }

          // if disposal == 3 (restore to previous), backup current composite
          if (disposal == 3)
          {
            var copy = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
            var dvCopy = new DrawingVisual();
            using (var dc = dvCopy.RenderOpen())
            {
              dc.DrawImage(currentComposite, new Rect(0, 0, canvasW, canvasH));
            }
            copy.Render(dvCopy);
            backupComposite = copy.Clone();
            if (backupComposite.CanFreeze)
              backupComposite.Freeze();
          }

          // compose currentComposite + frame at (left, top)
          var dv = new DrawingVisual();
          using (var dc = dv.RenderOpen())
          {
            dc.DrawImage(currentComposite, new Rect(0, 0, canvasW, canvasH));
            dc.DrawImage(frame, new Rect(left, top, fw, fh));
          }
          var composed = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
          composed.Render(dv);
          if (composed.CanFreeze)
            composed.Freeze();

          // scale to targetSize x targetSize preserving aspect by letterbox (fit inside)
          var scaled = ScaleBitmapSourceToSquare(composed, targetSize);
          if (scaled.CanFreeze)
            scaled.Freeze();

          list.Add((scaled, delayMs));

          // apply disposal rules to update currentComposite for next frame
          if (disposal == 2)
          {
            // restore to background (clear rect)
            var dv2 = new DrawingVisual();
            using (var dc = dv2.RenderOpen())
            {
              dc.DrawImage(currentComposite, new Rect(0, 0, canvasW, canvasH));
              dc.DrawRectangle(Brushes.Transparent, null, new Rect(left, top, fw, fh));
            }
            var next = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
            next.Render(dv2);
            currentComposite = next;
          }
          else if (disposal == 3)
          {
            if (backupComposite != null)
            {
              var dv2 = new DrawingVisual();
              using (var dc = dv2.RenderOpen())
              {
                dc.DrawImage(backupComposite, new Rect(0, 0, canvasW, canvasH));
              }
              var next = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
              next.Render(dv2);
              currentComposite = next;
            }
            // else leave currentComposite as is
          }
          else
          {
            // disposal 0 or 1: keep composed as currentComposite
            currentComposite = composed;
          }
        } // for frames
      } // using fs

      return list;
    }

    // Scale a BitmapSource into a square targetSize x targetSize, center content, preserve aspect
    private static BitmapSource ScaleBitmapSourceToSquare(BitmapSource src, int targetSize)
    {
      double scale = Math.Min((double)targetSize / src.PixelWidth, (double)targetSize / src.PixelHeight);
      double w = src.PixelWidth * scale;
      double h = src.PixelHeight * scale;
      double offsetX = (targetSize - w) / 2.0;
      double offsetY = (targetSize - h) / 2.0;

      var dv = new DrawingVisual();
      using (var dc = dv.RenderOpen())
      {
        // Ð?m b?o ch?t lu?ng ?nh cao nh?t khi scale
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, targetSize, targetSize));
        dc.DrawImage(src, new Rect(offsetX, offsetY, w, h));
      }

      var target = new RenderTargetBitmap(targetSize, targetSize, 96, 96, PixelFormats.Pbgra32);
      target.Render(dv);
      return target;
    }
  }
  public class PushButtonGifAnimatorOptimized : IDisposable
  {
    private readonly PushButton _pushButton;
    private readonly Dispatcher _uiDispatcher;
    private System.Threading.Timer _timer;
    private List<(System.Windows.Media.Imaging.BitmapSource Frame, int DelayMs)> _frames;
    private int _index = 0;
    private int _isUpdating = 0;
    private CancellationTokenSource _cts;
    private volatile bool _loaded = false;
    private readonly int _minDelayMs = 1; // enforce minimum delay to avoid too fast scheduling

    // Constructor starts decoding on STA thread asynchronously.
    // gifPath: path to GIF file
    // initialIntervalMs: fallback interval until first frame is ready (not critical)
    // targetSize: pixel size for icon (e.g., 32)
    public PushButtonGifAnimatorOptimized(PushButton pushButton, string gifPath, int initialIntervalMs = 30, int targetSize = 32)
    {
      _pushButton = pushButton ?? throw new ArgumentNullException(nameof(pushButton));
      _uiDispatcher = Dispatcher.CurrentDispatcher;
      _cts = new CancellationTokenSource();

      // start decode on STA thread; when done, start timer scheduling by per-frame delays
      Task.Run(async () =>
      {
        try
        {
          var frames = await PushButtonGifDecoder.DecodeGifFramesScaledAsync(gifPath, targetSize, _cts.Token).ConfigureAwait(false);
          if (frames == null || frames.Count == 0)
            return;

          // copy into local list
          _frames = frames;

          // mark loaded & set initial image on UI thread
          await _uiDispatcher.BeginInvoke(new Action(() =>
          {
            try
            {
              _pushButton.LargeImage = _frames[0].Frame;
              // Th? thu?t ép Ribbon v? l?i ngay l?p t?c
              if (Autodesk.Windows.ComponentManager.Ribbon != null)
              {
                Autodesk.Windows.ComponentManager.Ribbon.UpdateLayout();
              }
              Interlocked.Exchange(ref _isUpdating, 0);
            }
            catch { }
          }), DispatcherPriority.Render);

          _loaded = true;

          // start background timer scheduling the first transition after frames[0].DelayMs
          int firstDelay = Math.Max(_minDelayMs, _frames[0].DelayMs);
          _timer = new System.Threading.Timer(TimerCallback, null, firstDelay, Timeout.Infinite);
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.WriteLine("Animator decode/start error: " + ex);
        }
      });
    }

    // Timer callback runs on ThreadPool thread
    private void TimerCallback(object state)
    {
      if (!_loaded || _frames == null || _frames.Count == 0)
        return;

      // guard reentrancy: if already invoking UI update, skip this tick
      if (Interlocked.Exchange(ref _isUpdating, 1) == 1)
        return;

      try
      {
        // advance index
        _index = (_index + 1) % _frames.Count;
        var frame = _frames[_index];

        // marshal UI update; when UI update completes, schedule next timer with next frame delay
        _uiDispatcher.BeginInvoke(new Action(() =>
        {
          try
          {
            _pushButton.LargeImage = frame.Frame;
          }
          catch { /* ignore */ }
          finally
          {
            Interlocked.Exchange(ref _isUpdating, 0);

            // schedule next tick from threadpool (timer.Change)
            int nextDelay = Math.Max(_minDelayMs, _frames[_index].DelayMs);
            try
            {
              _timer?.Change(nextDelay, Timeout.Infinite);
            }
            catch { /* timer disposed possibly */ }
          }
        }), DispatcherPriority.Render);
      }
      catch
      {
        Interlocked.Exchange(ref _isUpdating, 0);
      }
    }

    public void Dispose()
    {
      try
      {
        _cts?.Cancel();
      }
      catch { }
      _cts?.Dispose();
      _timer?.Dispose();
      _timer = null;
      _frames?.Clear();
      _frames = null;
    }

    public void Pause()
    {
      try
      {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
      }
      catch { }
    }

    public void Resume()
    {
      if (!_loaded || _frames == null || _frames.Count == 0)
        return;
      int nextDelay = Math.Max(_minDelayMs, _frames[_index].DelayMs);
      try
      {
        _timer?.Change(nextDelay, Timeout.Infinite);
      }
      catch { }
    }


  }

  [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
  [Autodesk.Revit.Attributes.Regeneration(Autodesk.Revit.Attributes.RegenerationOption.Manual)]
  [Autodesk.Revit.Attributes.Journaling(Autodesk.Revit.Attributes.JournalingMode.NoCommandData)]
  class NMKMTOApp : IExternalApplication
  {
    static string pathCompany = @"C:\ProgramData\Autodesk\ApplicationPlugins\NMKMTO.bundle";
    static string tabName = "NMKMTO";
    static string pathDll = string.Empty;
    static string pathImage = Path.Combine(pathCompany, "Contents", "Resources");
    public static bool ShouldOverrideTempHideIsolateDialog = false;
    static void get_pathdll_version(UIControlledApplication application)
    {
      try
      {
        pathDll = Path.Combine(pathCompany, "Contents", application.ControlledApplication.VersionNumber);
      }
      catch (Exception)
      {

      }
    }

    private static System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
    {
      string assemblyFullName;
      System.Reflection.AssemblyName assemblyName;

      assemblyName = new System.Reflection.AssemblyName(args.Name);
      assemblyFullName = System.IO.Path.Combine(pathDll, assemblyName.Name + ".dll");

      if (System.IO.File.Exists(assemblyFullName))
      {
        try
        {
          // Use LoadFrom instead of LoadFile to resolve dependencies
          return System.Reflection.Assembly.LoadFrom(assemblyFullName);
        }
        catch (Exception)
        {
          // Fallback: read bytes and load
          try
          {
            byte[] assemblyBytes = System.IO.File.ReadAllBytes(assemblyFullName);
            return System.Reflection.Assembly.Load(assemblyBytes);
          }
          catch
          {
            return null;
          }
        }
      }

      // Return null if the assembly cannot be resolved
      return null;
    }

    //private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
    //{
    //  // L?y tên assembly c?n resolve
    //  string assemblyName = new AssemblyName(args.Name).Name;

    //  // Ðu?ng d?n d?n thu m?c ch?a add-in
    //  string addinFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    //  // Tìm file DLL
    //  string assemblyPath = Path.Combine(addinFolder, assemblyName + ".dll");

    //  if (File.Exists(assemblyPath))
    //  {
    //    return Assembly.LoadFrom(assemblyPath);
    //  }

    //  return null;
    //}

    public Result OnStartup(UIControlledApplication application)
    {
      try
      {
        get_pathdll_version(application);
        AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);
        AddRibbonPanel(application);
        return Result.Succeeded;
      }
      catch (Exception ex)
      {
        TaskDialog.Show("Ribbon", ex.ToString());
        return Result.Failed;
      }
    }


    private PushButtonGifAnimatorOptimized _animator_Step;
    //------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //Create a push button to trigger a command add it to the ribbon panel.
    public void AddRibbonPanel(UIControlledApplication application)
    {
      application.CreateRibbonTab(tabName);

      RibbonPanel ribbonTool = application.CreateRibbonPanel(tabName, "Tools");
      RibbonPanel ribbonGeneral = application.CreateRibbonPanel(tabName, "General");

      try
      {
        PushButton pushButton = ribbonTool.AddItem(create_pushdata_Image(new List<string>() { "NMKMTO.dll", "Create column", "NMKMTO.NMKMTO_Revit", "StepAutomation.png", "A tool that automatically extracts and updates floor step (stepped slab) elevation values." })) as PushButton;

        string gifPath = $@"{pathImage}\StepAutomation.gif";
        if (File.Exists(gifPath))
          _animator_Step = new PushButtonGifAnimatorOptimized(pushButton, gifPath); // interval 120 ms
      }
      catch (Exception ex) { TaskDialog.Show("ERROR", ex.Message, TaskDialogCommonButtons.Ok); }

      var tab = ComponentManager.Ribbon.FindTab("Modify");
      if (tab != null)
      {
        var adwPanel = new Autodesk.Windows.RibbonPanel();
        adwPanel.CopyFrom(GetRibbonPanel(ribbonTool));
        tab.Panels.Add(adwPanel);

        var adwPanel_ = new Autodesk.Windows.RibbonPanel();
        adwPanel_.CopyFrom(GetRibbonPanel(ribbonGeneral));
        tab.Panels.Add(adwPanel_);
      }
    }

    private static readonly FieldInfo RibbonPanelField = typeof(Autodesk.Revit.UI.RibbonPanel).GetField("m_RibbonPanel", BindingFlags.Instance | BindingFlags.NonPublic);

    public static Autodesk.Windows.RibbonPanel GetRibbonPanel(Autodesk.Revit.UI.RibbonPanel panel)
    {
      return RibbonPanelField.GetValue(panel) as Autodesk.Windows.RibbonPanel;
    }

    public PushButtonData create_pushdata(List<string> infor_addin) //name_file_dll | name_in_UI | namepace.class | name_image | tooltip
    {
      PushButtonData push = null;
      try
      {
        // define 3 new buttons to be added as stacked buttons
        string GetQuantityDllPath = Path.Combine(pathDll, infor_addin[0]);
        push = new PushButtonData(infor_addin[1], infor_addin[1], GetQuantityDllPath, infor_addin[2]);
        //Uri imageSourseGetQuantity = new Uri(Path.Combine(pathImage, infor_addin[3]));
        //var bm = ResizeBitmap(new BitmapImage(imageSourseGetQuantity), 32);
        //push.LargeImage = bm;
        push.ToolTip = infor_addin[4];
        ContextualHelp contextualHelp = new ContextualHelp(ContextualHelpType.Url, "https://NMKMTO.com.au/");
        push.SetContextualHelp(contextualHelp);
      }
      catch (Exception)
      {

      }
      return push;
    }

    public PushButtonData create_pushdata_Image(List<string> infor_addin) //name_file_dll | name_in_UI | namepace.class | name_image | tooltip
    {
      PushButtonData push = null;
      try
      {
        // define 3 new buttons to be added as stacked buttons
        string GetQuantityDllPath = Path.Combine(pathDll, infor_addin[0]);
        push = new PushButtonData(infor_addin[1], infor_addin[1], GetQuantityDllPath, infor_addin[2]);
        Uri imageSourseGetQuantity = new Uri(Path.Combine(pathImage, infor_addin[3]));
        var bm = ResizeBitmap(new BitmapImage(imageSourseGetQuantity), 32);
        push.LargeImage = bm;
        push.ToolTip = infor_addin[4];
        ContextualHelp contextualHelp = new ContextualHelp(ContextualHelpType.Url, "https://NMKMTO.com.au/");
        push.SetContextualHelp(contextualHelp);
      }
      catch (Exception)
      {

      }
      return push;
    }

    public PushButtonData create_pushdata_pull(List<string> infor_addin) //name_file_dll | name_in_UI | namepace.class | name_image | tooltip
    {
      PushButtonData push = null;
      try
      {
        // define 3 new buttons to be added as stacked buttons
        string GetQuantityDllPath = Path.Combine(pathDll, infor_addin[0]);
        push = new PushButtonData(infor_addin[1], infor_addin[1], GetQuantityDllPath, infor_addin[2]);
        Uri imageSourseGetQuantity = new Uri(Path.Combine(pathImage, infor_addin[3]));
        var bm = ResizeBitmap(new BitmapImage(imageSourseGetQuantity), 32);
        push.LargeImage = bm;
        push.ToolTip = infor_addin[4];
        ContextualHelp contextualHelp = new ContextualHelp(ContextualHelpType.Url, "https://NMKMTO.com.au/");
        push.SetContextualHelp(contextualHelp);
      }
      catch (Exception)
      {

      }
      return push;
    }

    public PulldownButtonData add_items(List<string> infor_addin) //name_in_UI | name_image | tooltip
    {
      PulldownButtonData pull_down = null;
      try
      {
        // add a pull-down button to the panel 
        pull_down = new PulldownButtonData(infor_addin[0], infor_addin[0]);
        Uri imageSourseGetQuantity = new Uri(Path.Combine(pathImage, infor_addin[1]));
        var bm = ResizeBitmap(new BitmapImage(imageSourseGetQuantity), 32);
        pull_down.LargeImage = new BitmapImage(imageSourseGetQuantity);
        pull_down.ToolTip = infor_addin[2];
        ContextualHelp contextualHelp = new ContextualHelp(ContextualHelpType.Url, "https://NMKMTO.com.au/");
        pull_down.SetContextualHelp(contextualHelp);
      }
      catch (Exception)
      {

      }
      return pull_down;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
      _animator_Step.Dispose();
      AppDomain.CurrentDomain.AssemblyResolve -= new ResolveEventHandler(CurrentDomain_AssemblyResolve);
      return Result.Succeeded;
    }

    public BitmapSource ResizeBitmap(BitmapSource source, int maxSize)
    {
      double ratio = Math.Min((double)maxSize / source.PixelWidth, (double)maxSize / source.PixelHeight);
      if (ratio >= 1.0)
        return source;

      int newWidth = (int)(source.PixelWidth * ratio);
      int newHeight = (int)(source.PixelHeight * ratio);

      var rect = new System.Windows.Rect(0, 0, newWidth, newHeight);

      // t?o DrawingVisual d? render ?nh m?i
      var dv = new DrawingVisual();
      using (DrawingContext dc = dv.RenderOpen())
      {
        dc.DrawImage(source, rect);
      }

      var rtb = new RenderTargetBitmap(newWidth, newHeight,
                                       source.DpiX, source.DpiY, // ho?c set c? d?nh DPI n?u mu?n
                                       PixelFormats.Pbgra32);
      rtb.Render(dv);
      rtb.Freeze();
      return rtb;
    }
  }
}

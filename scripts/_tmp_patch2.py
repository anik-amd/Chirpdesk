p='src/Chirpdesk/Hosting/HostWindow.xaml.cs'
s=open(p,encoding='utf-8').read()
s=s.replace('    private void ToggleMaximize()\n', '    public void ToggleMaximizeForTest() => ToggleMaximize();\n    private void ToggleMaximize()\n',1)
open(p,'w',encoding='utf-8',newline='').write(s)

p='src/Chirpdesk/App.xaml.cs'
s=open(p,encoding='utf-8').read()
old='''        _log.Info("started");

        if (!string.IsNullOrEmpty(options.Page))'''
new='''        _log.Info("started");

        if (options.Maximize)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => _host?.ToggleMaximizeForTest()));
        }

        if (!string.IsNullOrEmpty(options.Page))'''
assert old in s
s=s.replace(old,new,1)
open(p,'w',encoding='utf-8',newline='').write(s)
print('ok')
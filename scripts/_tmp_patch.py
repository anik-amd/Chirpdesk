import re
p='src/Chirpdesk/StartupOptions.cs'
s=open(p,encoding='utf-8').read()
s=s.replace('    bool WarmAll,\n    bool Autostart,\n    string? Page)\n', '    bool WarmAll,\n    bool Autostart,\n    string? Page,\n    bool Maximize)\n')
s=s.replace('        var autostart = Array.Exists(args, a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));\n', '        var autostart = Array.Exists(args, a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));\n        var maximize = Array.Exists(args, a => a.Equals("--maximize", StringComparison.OrdinalIgnoreCase));\n')
s=s.replace('        return new StartupOptions(selfTest, testToast, soak, open, quitAfter, hidden, notifyTest, warmAll, autostart, page);',
            '        return new StartupOptions(selfTest, testToast, soak, open, quitAfter, hidden, notifyTest, warmAll, autostart, page, maximize);')
open(p,'w',encoding='utf-8',newline='').write(s)
print('ok')
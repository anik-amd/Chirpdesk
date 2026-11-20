using System.Text.Json;

namespace Chirpdesk.Core.Userscripts;

/// <summary>One enabled script the manager returned for a page. The body comes from the plugin.</summary>
public sealed record InjectedScript(string Id, string Name, string Version, string Code);

/// <summary>
/// Wraps a manager-provided body with the small GM surface. This is the only code the host
/// adds around a user script; it never loads anything as a .NET assembly.
/// </summary>
public static class UserscriptWrapper
{
    public static string Build(InjectedScript script)
    {
        var id = JsonSerializer.Serialize(script.Id);
        var name = JsonSerializer.Serialize(script.Name);
        var version = JsonSerializer.Serialize(script.Version);

        return $$"""
            (function () {
              window.__chirpdeskScripts = window.__chirpdeskScripts || {};
              if (window.__chirpdeskScripts[{{id}}]) { return; }
              window.__chirpdeskScripts[{{id}}] = true;
              var prefix = 'chirpdesk.userscript.' + {{id}} + '.';
              var unsafeWindow = window;
              var GM_info = { script: { name: {{name}}, version: {{version}} } };
              function GM_addStyle(css) {
                var style = document.createElement('style');
                style.textContent = css;
                document.documentElement.appendChild(style);
                return style;
              }
              function GM_setValue(key, value) {
                try { localStorage.setItem(prefix + key, JSON.stringify(value)); } catch (e) { }
              }
              function GM_getValue(key, fallback) {
                try {
                  var raw = localStorage.getItem(prefix + key);
                  return raw === null ? fallback : JSON.parse(raw);
                } catch (e) { return fallback; }
              }
              function GM_deleteValue(key) {
                try { localStorage.removeItem(prefix + key); } catch (e) { }
              }
              function GM_log() {
                try { console.log.apply(console, arguments); } catch (e) { }
              }
              try {
                (function () {
            {{script.Code}}
                })();
              } catch (e) {
                console.warn('Chirpdesk userscript failed: ' + {{name}}, e);
              }
            })();
            """;
    }
}

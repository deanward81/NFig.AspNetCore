﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using NFig.UI;

namespace NFig.AspNetCore
{
    /// <summary>
    /// Middleware that renders the NFig UI settings pages.
    /// </summary>
    /// <remarks>
    /// This middleware handles the following routes in an application:
    /// 
    ///  - GET {prefix} -> renders the settings page
    ///  - GET {prefix}/js -> renders the javascript for the page
    ///  - GET {prefix}/json -> renders the JSON representing the settings within the application
    ///  - POST {prefix}/set -> sets an override for a setting
    ///  - POST {prefix}/clear -> clears an override for a setting
    /// </remarks>

    public static class NFigMiddleware<TSettings, TTier, TDataCenter>
        where TSettings : class, INFigSettings<TTier, TDataCenter>, new()
        where TTier : struct
        where TDataCenter : struct
    {
        private static readonly string _htmlTemplate;
        private static readonly ImmutableDictionary<string, HandleRequestDelegate> _handlers;

        private delegate Task HandleRequestDelegate(HttpContext ctx, NFigSettingsWithStore<TSettings, TTier, TDataCenter> settingsWithStore);

        static NFigMiddleware()
        {
            _handlers = new Dictionary<string, HandleRequestDelegate>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = IndexAsync,
                ["json"] = Json,
                ["set"] = SetOverrideAsync,
                ["clear"] = ClearOverrideAsync,
                ["js"] = JavascriptAsync,
            }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

            _htmlTemplate = GetEmbeddedResource(typeof(NFigMiddleware<,,>).Namespace + ".settings.html");
        }

        private static string GetEmbeddedResource(string name)
        {
            using (var stream = typeof(NFigMiddleware<,,>).Assembly.GetManifestResourceStream(name))
            using (var streamReader = new StreamReader(stream))
            {
                return streamReader.ReadToEnd();
            }
        }

        public static Task HandleRequestAsync(HttpContext ctx)
        {
            // In MVC requests, PathInfo isn't set - determine via Path..
            // e.g. "/errors/info" or "/errors/"
            var match = Regex.Match(ctx.Request.Path, @"/?(?<resource>[\w\-\.]+)/?$");
            var resource = match.Success ? match.Groups["resource"].Value.ToLower(CultureInfo.InvariantCulture) : string.Empty;

            if (!NFigSettingsCache.TryGet<TSettings, TTier, TDataCenter>(out var settingsWithStore))
            {
                return NotFound(ctx);
            }

            if (_handlers.TryGetValue(resource, out var handler))
            {
                return handler(ctx, settingsWithStore);
            }

            return _handlers[string.Empty](ctx, settingsWithStore);
        }

        private static Task NotFound(HttpContext ctx)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        }

        private static Task IndexAsync(HttpContext ctx, NFigSettingsWithStore<TSettings, TTier, TDataCenter> settingsWithStore)
        {
            if (ctx.Request.Method != "GET")
            {
                return NotFound(ctx);
            }

            var html = _htmlTemplate
                .Replace("{{ApplicationName}}", settingsWithStore.Settings.ApplicationName)
                .Replace("{{Tier}}", settingsWithStore.Settings.Tier.ToString())
                .Replace("{{DataCenter}}", settingsWithStore.Settings.DataCenter.ToString())
                .Replace("{{Prefix}}", ctx.Request.Path.Value);

            return HtmlAsync(ctx, html);
        }

        private static Task Json(HttpContext ctx, NFigSettingsWithStore<TSettings, TTier, TDataCenter> settingsWithStore)
        {
            if (ctx.Request.Method != "GET")
            {
                return NotFound(ctx);
            }

            var json = settingsWithStore.Store.GetSettingsJson(
                settingsWithStore.Settings.ApplicationName,
                settingsWithStore.Settings.Tier,
                settingsWithStore.Settings.DataCenter,
                (TDataCenter[])Enum.GetValues(typeof(TDataCenter))
            );

            return JsonAsync(ctx, json);
        }

        private static Task JavascriptAsync(HttpContext ctx, NFigSettingsWithStore<TSettings, TTier, TDataCenter> settingsWithStore)
        {
            if (ctx.Request.Method != "GET")
            {
                return NotFound(ctx);
            }

            return JsonAsync(ctx, NFigUI.SettingsPanelScript);
        }

        private class SettingData
        {
            public string SettingName { get; set; }
            public string Value { get; set; }
            public TDataCenter DataCenter { get; set; }
        }

        private static async Task<T> ReadJsonAsync<T>(HttpRequest request)
        {
            using (var reader = new StreamReader(request.Body, Encoding.UTF8))
            { 
                return JsonConvert.DeserializeObject<T>(await reader.ReadToEndAsync());
            }
        }

        private static async Task SetOverrideAsync(HttpContext ctx, NFigSettingsWithStore<TSettings, TTier, TDataCenter> settingsWithStore)
        {
            if (ctx.Request.Method != "POST")
            {
                await NotFound(ctx);
                return;
            }

            var settingData = await ReadJsonAsync<SettingData>(ctx.Request);
            if (string.IsNullOrEmpty(settingData.SettingName))
            {
                await BadRequestAsync(ctx, "Invalid setting name specified");
                return;
            }

            if (!Enum.IsDefined(typeof(TDataCenter), settingData.DataCenter))
            {
                await BadRequestAsync(ctx, "Invalid data center specified");
                return;
            }

            var settingInfo = settingsWithStore.Store.GetSettingInfo(
                settingsWithStore.Settings.ApplicationName,
                settingData.SettingName
            );

            if (!settingInfo.CanSetOverrideFor(settingsWithStore.Settings.Tier, settingData.DataCenter))
            {
                await NotImplementedAsync(ctx, $"Setting {settingData.SettingName} does not allow overrides for Data Center {settingData.DataCenter}");
                return;
            }

            if (!settingsWithStore.Store.IsValidStringForSetting(settingData.SettingName, settingData.Value))
            {
                await ConflictAsync(ctx, $"\"{settingData.Value}\" is an invalid value for setting {settingData.SettingName}");
                return;
            }

            settingsWithStore.Store.SetOverride(
                settingsWithStore.Settings.ApplicationName,
                settingData.SettingName,
                settingData.Value,
                settingsWithStore.Settings.Tier,
                settingData.DataCenter
            );

            var json = settingsWithStore.Store.GetSettingJson(
                settingsWithStore.Settings.ApplicationName,
                settingData.SettingName,
                settingsWithStore.Settings.Tier,
                settingsWithStore.Settings.DataCenter,
                (TDataCenter[])Enum.GetValues(typeof(TDataCenter))
            );

            await JsonAsync(ctx, json);
            return;
        }

        private static async Task ClearOverrideAsync(HttpContext ctx, NFigSettingsWithStore<TSettings, TTier, TDataCenter> settingsWithStore)
        {
            if (ctx.Request.Method != "POST")
            {
                await NotFound(ctx);
                return;
            }

            var settingData = await ReadJsonAsync<SettingData>(ctx.Request);
            if (string.IsNullOrEmpty(settingData.SettingName))
            {
                await BadRequestAsync(ctx, "Invalid setting name specified");
                return;
            }

            if (!Enum.IsDefined(typeof(TDataCenter), settingData.DataCenter))
            {
                await BadRequestAsync(ctx, "Invalid data center specified");
                return;
            }

            settingsWithStore.Store.ClearOverride(
                settingsWithStore.Settings.ApplicationName,
                settingData.SettingName,
                settingsWithStore.Settings.Tier,
                settingData.DataCenter
            );

            var json = settingsWithStore.Store.GetSettingJson(
                settingsWithStore.Settings.ApplicationName,
                settingData.SettingName,
                settingsWithStore.Settings.Tier,
                settingsWithStore.Settings.DataCenter,
                (TDataCenter[])Enum.GetValues(typeof(TDataCenter))
            );

            await JsonAsync(ctx, json);
            return;
        }

        private static Task BadRequestAsync(HttpContext ctx, string content)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            ctx.Response.ContentType = "text/plain";
            return ctx.Response.WriteAsync(content);
        }

        private static Task NotImplementedAsync(HttpContext ctx, string content)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotImplemented;
            ctx.Response.ContentType = "text/plain";
            return ctx.Response.WriteAsync(content);
        }

        private static Task ConflictAsync(HttpContext ctx, string content)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.Conflict;
            ctx.Response.ContentType = "text/plain";
            return ctx.Response.WriteAsync(content);
        }

        private static Task HtmlAsync(HttpContext ctx, string html)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.ContentType = "text/html";
            return ctx.Response.WriteAsync(html);
        }

        private static Task JsonAsync(HttpContext ctx, string json)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(json);
        }

        private static Task JavascriptAsync(HttpContext ctx, string js)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.ContentType = "text/javascript";
            return ctx.Response.WriteAsync(js);
        }
    }
}
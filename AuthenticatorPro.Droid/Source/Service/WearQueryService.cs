// Copyright (C) 2021 jmh
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Gms.Wearable;
using AuthenticatorPro.Droid.Data;
using AuthenticatorPro.Droid.Data.Source;
using AuthenticatorPro.Droid.Util;
using AuthenticatorPro.Droid.Shared.Query;
using Newtonsoft.Json;
using SQLite;

namespace AuthenticatorPro.Droid.Service
{
    [Service]
    [IntentFilter(
        new[] { MessageApi.ActionMessageReceived },
        DataScheme = "wear",
        DataHost = "*"
    )]
    internal class WearQueryService : WearableListenerService
    {
        private const string GetSyncBundleCapability = "get_sync_bundle";
        private const string GetCustomIconCapability = "get_custom_icon";

        private bool _shouldCloseDatabase;
        private readonly Lazy<Task> _initTask;

        private AuthenticatorSource _authSource;
        private CategorySource _categorySource;
        private CustomIconSource _customIconSource;
        
        public WearQueryService()
        {
            _shouldCloseDatabase = false;
            
            _initTask = new Lazy<Task>(async delegate
            {
                SQLiteAsyncConnection connection;
                
                if(Database.IsOpen)
                    connection = Database.GetConnection();
                else
                {
                    var password = await SecureStorageWrapper.GetDatabasePassword();
                    connection = await Database.Open(password);
                    _shouldCloseDatabase = true;
                }
                
                _customIconSource = new CustomIconSource(connection);
                _categorySource = new CategorySource(connection);
                _authSource = new AuthenticatorSource(connection);
            });
        }

        public override async void OnDestroy()
        {
            base.OnDestroy();

            if(_shouldCloseDatabase)
                await Database.Close();
        }

        private async Task GetSyncBundle(string nodeId)
        {
            await _authSource.Update();
            var auths = new List<WearAuthenticator>();
            
            foreach(var auth in _authSource.GetView())
            {
                var bindings = _authSource.CategoryBindings
                    .Where(c => c.AuthenticatorSecret == auth.Secret)
                    .Select(c => new WearAuthenticatorCategory(c.CategoryId, c.Ranking))
                    .ToList();
                
                var item = new WearAuthenticator(
                    auth.Type, auth.Secret, auth.Icon, auth.Issuer, auth.Username, auth.Period, auth.Digits, auth.Algorithm, auth.Ranking, bindings); 
                
                auths.Add(item);
            }
            
            await _categorySource.Update();
            var categories = _categorySource.GetAll().Select(c => new WearCategory(c.Id, c.Name)).ToList();

            await _customIconSource.Update();
            var customIconIds = _customIconSource.GetAll().Select(i => i.Id).ToList();
            
            var preferenceWrapper = new PreferenceWrapper(this);
            var preferences = new WearPreferences(preferenceWrapper.DefaultCategory, preferenceWrapper.SortMode);
            
            var bundle = new WearSyncBundle(auths, categories, customIconIds, preferences);
            
            var json = JsonConvert.SerializeObject(bundle);
            var data = Encoding.UTF8.GetBytes(json);

            await WearableClass.GetMessageClient(this).SendMessageAsync(nodeId, GetSyncBundleCapability, data);
        }

        private async Task GetCustomIcon(string customIconId, string nodeId)
        {
            await _customIconSource.Update();
            var icon = _customIconSource.Get(customIconId);
            
            var data = Array.Empty<byte>();

            if(icon != null)
            {
                var response = new WearCustomIcon(icon.Id, icon.Data);
                var json = JsonConvert.SerializeObject(response);
                data = Encoding.UTF8.GetBytes(json);
            }

            await WearableClass.GetMessageClient(this).SendMessageAsync(nodeId, GetCustomIconCapability, data);
        }

        public override async void OnMessageReceived(IMessageEvent messageEvent)
        {
            await _initTask.Value;

            switch(messageEvent.Path)
            {
                case GetSyncBundleCapability:
                    await GetSyncBundle(messageEvent.SourceNodeId);
                    break;
                
                case GetCustomIconCapability:
                {
                    var id = Encoding.UTF8.GetString(messageEvent.GetData());
                    await GetCustomIcon(id, messageEvent.SourceNodeId);
                    break;
                }
            }
        }
    }
}
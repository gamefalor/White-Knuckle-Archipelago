using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Exceptions;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Models;
using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static CL_GameTracker;
using static SessionEventModule_SendMessageToModules;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

namespace WKRando;

public class ArchipelagoClient
{
    private static ArchipelagoSession _session = ArchipelagoSessionFactory.CreateSession("localhost", 38281);
    private static string _servername = "localhost:38281";
    private static string _username = string.Empty;
    private static string _password = string.Empty;
    public static DeathLinkService _deathlinkservice;
    
    private static bool _connectedBefore;
    public static bool Connected;
    private static int _reconnectAttempts = 0;
    private static int _slot = -1;

    private static Dictionary<string, object> slotData; // might be needed later, who knows? i do, i need it later

    private static Queue<ItemInfo> _items = [];
    private static List<long> _locationsToSend = [];

    private static void NewSession(string server)
    {
        _session = ArchipelagoSessionFactory.CreateSession(server);
        _servername = server;
    }
    
    //Archipelago connection procedure using Multiclient.net 
    public static async Task<object> Connect(string server = null, string user = null, string pass = null)
    {
        
        
        if (Connected)
        {
            CommandConsole.Log($"Already connected to server {_servername} as {_username}");
            return null;
        }
        
        
        Plugin.Logger.LogInfo("Connecting to " + server);

        if (server != null && server != _servername)
        {
            NewSession(server);
        }
        if (user != null && user != _username) 
            _username = user;
        if (pass != null && pass != _password) 
            _password = pass;
        
        
        _session.Items.ItemReceived += OnItemReceive;
        _session.MessageLog.OnMessageReceived += OnMessageReceive;
        _session.Socket.ErrorReceived += OnError;
        _session.Socket.SocketClosed += OnSocketClosed;
        _session.Locations.CheckedLocationsUpdated += OnLocationReceive;

        APItems.TargetAPDebuffCount = 10;
        APItems.ClearAllFlags();
        APItems.SentLocations.Clear();
        
        LoginResult result;

        
        try
        {
            result = _session.TryConnectAndLogin("White Knuckle", _username, ItemsHandlingFlags.AllItems);
        }
        catch (Exception e)
        {
            result = new LoginFailure(e.GetBaseException().Message);
        }

        
        if (!result.Successful)
        {
            LoginFailure failure = (LoginFailure)result;
            CommandConsole.Log($"Failed to Connect to {_servername} as {_username}:");
            foreach (string error in failure.Errors)
            {
                CommandConsole.Log($"    {error}");
            }

            foreach (ConnectionRefusedError error in failure.ErrorCodes)
            {
                CommandConsole.Log($"    {error}");
            }
            
            await Disconnect();
            return null;
        }
        
        Connected = true;
        
        var loginSuccess = (LoginSuccessful)result;
        _slot = loginSuccess.Slot;

        APItems.SentLocations.AddRange(_session.Locations.AllLocationsChecked);
        FixSendQueue();
        foreach (var item in _session.Items.AllItemsReceived)
        {
            _items.Enqueue(item);
        }
        CheckReceivedItems();
        foreach (long item in APItems.SentLocations)
        {
            Plugin.Logger.LogInfo($"Logged Sent Location ID: {item}");
        }
        
        CommandConsole.Log($"Successfully connected to {_servername} as {_username}!");
        CommandConsole.Log($"   Slot Number: {loginSuccess.Slot}");

        slotData = loginSuccess.SlotData;
        FillOptions();

        _connectedBefore = true;

        _deathlinkservice = _session.CreateDeathLinkService();
        _deathlinkservice.OnDeathLinkReceived += (deathLinkObject) => {
            Deathlink.ProcDeathlink(deathLinkObject);
        };
        return null;
    }

    
    
    public static async Task<object> Disconnect(bool tryReconnect = false)
    {
        
        if (_session != null)
        {
            _session.Items.ItemReceived -= OnItemReceive;
            _session.MessageLog.OnMessageReceived -= OnMessageReceive;
            _session.Socket.ErrorReceived -= OnError;
            _session.Socket.SocketClosed -= OnSocketClosed;
            _session.Locations.CheckedLocationsUpdated -= OnLocationReceive;
            
            APItems.TargetAPDebuffCount = 10;
            APItems.ClearAllFlags();
            APItems.SentLocations.Clear();
        }
        
        Connected = false;
        
        
        if (_connectedBefore & tryReconnect)
        {
            _reconnectAttempts++;
            if (_reconnectAttempts >= 5)
            {
                await Task.Delay(5000);
                _connectedBefore = false;
            }

            await Connect();
        }

        slotData = new();

        return null;
    }

    //Main update loop for checking for checks
    public static void Update()
    {
        if (Connected)
        {
            try
            {
                CheckReceivedItems();
                CheckLocationsToSend();
            }
            catch 
            {
                _ = Disconnect();
            }
            
        }
    }

    private static void OnLocationReceive(ReadOnlyCollection<long> newCheckedLocations)
    {
        lock (APItems.SentLocations)
        {
            foreach (var newLoc in newCheckedLocations)
            {
                APItems.SentLocations.Add(newLoc);
            }
        }
    }
    
    private static void OnItemReceive(ReceivedItemsHelper helper)
    {
        while (helper.Any())
        {
            _items.Enqueue(helper.DequeueItem());
        }
        
    }

    private static void OnMessageReceive(LogMessage message)
    {
        CommandConsole.Log($"[{_servername}] - {message}"); 
    }
    
    public static void Say(string[] args)
    {
        if(Connected) {_session.Say(args[0]);}
        else {CommandConsole.Log("Not currently connected to server");}
    }

    public static void TryQueueLocation(long itemID)
    {
        _locationsToSend.Add(itemID);
    }
    
    private static void CheckReceivedItems()
    {
        
        while(_items.Any() && Connected)
        {
            ItemInfo item = _items.Dequeue();
            if (item.Player.Slot == _slot)
            {
                APItems.UpdateFromId(item.ItemId);
                CommandConsole.Log($"Received item: {item.ItemDisplayName} from {item.LocationName} in game {item.LocationGame}");
            }
            
        }
        
    }

    private static void CheckLocationsToSend()
    {
        if (_locationsToSend.Any() & Connected)
        {
            try
            {
                if (_locationsToSend.Any(l => l == 0xAB50108))
                {
                    _session.SetGoalAchieved();
                    return;
                }
                _session.Locations.CompleteLocationChecksAsync(_locationsToSend.ToArray());
                foreach (long l in _locationsToSend)
                {
                    if (!APItems.SentLocations.Contains(l))
                    {
                        APItems.SentLocations.Add(l);
                    }
                }
                _locationsToSend.Clear();
            }
            catch (ArchipelagoSocketClosedException e)
            {
                Plugin.Logger.LogError(e.ToString());
                Disconnect();
            }
        }
    }

    private static void OnError(Exception exception, string message)
    {
        Plugin.Logger.LogError(exception.ToString());
        CommandConsole.Log("AP " + message);
        
        Disconnect();
    }

    private static void OnSocketClosed(string message)
    {
        Plugin.Logger.LogError("AP " + message);
        CommandConsole.Log("AP " + message);
        
        Disconnect();
    }

    private static void FixSendQueue()
    {
        for (int i = 0; i < _locationsToSend.Count; i++) 
        {
            if (APItems.SentLocations.Contains(_locationsToSend[i]))
            {
                _locationsToSend.RemoveAt(i);
            }
        }
    }

    // writes all options from the ap server into variables accessible here
    private static void FillOptions()
    {
        string slotDataLogger = "";
        foreach (string I in slotData.Keys)
        {
            slotDataLogger += $"{I}: {slotData[I]} ({slotData[I].GetType()})\n"; 
        }
        Plugin.Logger.LogInfo(slotDataLogger);
        Plugin.Logger.LogInfo(Convert.ToString((bool)_session.DataStorage[Scope.Slot, "ConnectedOnce"]));

        _session.DataStorage[Scope.Slot, "ConnectedOnce"].Initialize(false); // sets it to be SOMETHING

        Plugin.ClientOptions.LoadOptions();

        if (!_session.DataStorage[Scope.Slot, "ConnectedOnce"])
        {
            Plugin.Logger.LogInfo("First connection detecting, setting any client data");
            SetClientOptions(new string[1]); // feels like evil coding, im sure this wont cause weird shit right?
        }

        _session.DataStorage[Scope.Slot, "ConnectedOnce"] = true; //causes client settings to not be changed upon future connections
    }
    
    public static void SetClientOptions(string[] args)
    {
        try
        {
            // why isnt this a switch block?? fuck if i know but it stopped working when i tried it
            if (Convert.ToInt32(slotData["deathlink"]) == 0)
            {
                Plugin.ClientOptions.EnableDeathlink();
            }
            else if (Convert.ToInt32(slotData["deathlink"]) == 1)
            {
                Plugin.ClientOptions.DisableDeathlink();
            }
        } catch { Plugin.Logger.LogInfo("Deathlink not found, skipping"); }

        try
        {
            if (Convert.ToInt32(slotData["deathlink_amnesty"]) != 0)
            {
                Plugin.ClientOptions.deathlink_amnesty = Convert.ToInt32(slotData["deathlink_amnesty"]);
            }
        } catch { Plugin.Logger.LogInfo("Deathlink amnesty not found, skipping"); }

        Plugin.ClientOptions.SaveOptions();
        if (Plugin.ClientOptions.deathlink) {_deathlinkservice.EnableDeathLink();}
    }

    public event DeathLinkService.DeathLinkReceivedHandler OnDeathLinkReceived { add { } remove { } }
}
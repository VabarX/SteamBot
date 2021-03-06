using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SteamKit2;
using SteamTrade.Exceptions;
using SteamTrade.TradeWebAPI;

namespace SteamTrade
{
    public partial class Trade
    {
        #region Static Public data
        public static Schema CurrentSchema = null;
        #endregion

        private const int WEB_REQUEST_MAX_RETRIES = 3;
        private const int WEB_REQUEST_TIME_BETWEEN_RETRIES_MS = 600;

        // list to store all trade events already processed
        private readonly List<TradeEvent> eventList;

        // current bot's sid
        private readonly SteamID mySteamId;

        private readonly Dictionary<int, ulong> myOfferedItems;
        private readonly List<ulong> steamMyOfferedItems;
        private readonly TradeSession session;

        internal Trade(SteamID me, SteamID other, string sessionId, string token, Inventory myInventory, Inventory otherInventory)
        {
            TradeStarted = false;
            OtherIsReady = false;
            MeIsReady = false;
            mySteamId = me;
            OtherSID = other;

            session = new TradeSession(sessionId, token, other);

            this.eventList = new List<TradeEvent>();

            OtherOfferedItems = new List<ulong>();
            myOfferedItems = new Dictionary<int, ulong>();
            steamMyOfferedItems = new List<ulong>();

            OtherInventory = otherInventory;
            MyInventory = myInventory;
        }

        #region Public Properties

        /// <summary>Gets the other user's steam ID.</summary> 
        public SteamID OtherSID { get; private set; }

        /// <summary>
        /// Gets the bot's Steam ID.
        /// </summary>
        public SteamID MySteamId
        {
            get { return mySteamId; }
        }

        /// <summary> 
        /// Gets the inventory of the other user. 
        /// </summary>
        public Inventory OtherInventory { get; private set; }

        /// <summary> 
        /// Gets the private inventory of the other user. 
        /// </summary>
        public ForeignInventory OtherPrivateInventory { get; private set; }

        /// <summary> 
        /// Gets the inventory of the bot.
        /// </summary>
        public Inventory MyInventory { get; private set; }

        /// <summary>
        /// Gets the items the user has offered, by itemid.
        /// </summary>
        /// <value>
        /// The other offered items.
        /// </value>
        public List<ulong> OtherOfferedItems { get; private set; }

        /// <summary>
        /// Gets a value indicating if the other user is ready to trade.
        /// </summary>
        public bool OtherIsReady { get; private set; }

        /// <summary>
        /// Gets a value indicating if the bot is ready to trade.
        /// </summary>
        public bool MeIsReady { get; private set; }

        /// <summary>
        /// Gets a value indicating if a trade has started.
        /// </summary>
        public bool TradeStarted { get; private set; }

        /// <summary>
        /// Gets a value indicating if the remote trading partner cancelled the trade.
        /// </summary>
        public bool OtherUserCancelled { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the trade completed normally. This
        /// is independent of other flags.
        /// </summary>
        public bool HasTradeCompletedOk { get; private set; }

        #endregion
                
        #region Public Events

        public delegate void CloseHandler ();

        public delegate void ErrorHandler (string error);

        public delegate void TimeoutHandler ();

        public delegate void SuccessfulInit ();

        public delegate void UserAddItemHandler (Schema.Item schemaItem,Inventory.Item inventoryItem);

        public delegate void UserRemoveItemHandler (Schema.Item schemaItem,Inventory.Item inventoryItem);

        public delegate void MessageHandler (string msg);

        public delegate void UserSetReadyStateHandler (bool ready);

        public delegate void UserAcceptHandler ();

        /// <summary>
        /// When the trade closes, this is called.  It doesn't matter
        /// whether or not it was a timeout or an error, this is called
        /// to close the trade.
        /// </summary>
        public event CloseHandler OnClose;
        
        /// <summary>
        /// This is for handling errors that may occur, like inventories
        /// not loading.
        /// </summary>
        public event ErrorHandler OnError;

        /// <summary>
        /// This occurs after Inventories have been loaded.
        /// </summary>
        public event SuccessfulInit OnAfterInit;

        /// <summary>
        /// This occurs when the other user adds an item to the trade.
        /// </summary>
        public event UserAddItemHandler OnUserAddItem;
        
        /// <summary>
        /// This occurs when the other user removes an item from the 
        /// trade.
        /// </summary>
        public event UserAddItemHandler OnUserRemoveItem;

        /// <summary>
        /// This occurs when the user sends a message to the bot over
        /// trade.
        /// </summary>
        public event MessageHandler OnMessage;

        /// <summary>
        /// This occurs when the user sets their ready state to either
        /// true or false.
        /// </summary>
        public event UserSetReadyStateHandler OnUserSetReady;

        /// <summary>
        /// This occurs when the user accepts the trade.
        /// </summary>
        public event UserAcceptHandler OnUserAccept;
        
        #endregion

        /// <summary>
        /// Cancel the trade.  This calls the OnClose handler, as well.
        /// </summary>
        public bool CancelTrade ()
        {
            bool success = RetryWebRequest(session.CancelTradeWebCmd);
            
            if (success && OnClose != null)
                OnClose ();

            return success;
        }

        /// <summary>
        /// Adds a specified TF2 item by its itemid.
        /// If the item is not a TF2 item, use the AddItem(ulong itemid, int appid, long contextid) overload
        /// </summary>
        /// <returns><c>false</c> if the tf2 item was not found in the inventory.</returns>
        public bool AddItem (ulong itemid)
        {
            if (MyInventory.GetItem(itemid) == null)
            {
                return false;
            }
            else
            {
                return AddItem(new TradeUserAssets(){assetid=itemid,appid=440,contextid=2});
            }
        }
        public bool AddItem(ulong itemid, int appid, long contextid)
        {
            return AddItem(new TradeUserAssets(){assetid=itemid,appid=appid,contextid=contextid});
        }
        public bool AddItem(TradeUserAssets item)
        {
            var slot = NextTradeSlot();
            bool success = RetryWebRequest(() => session.AddItemWebCmd(item.assetid, slot, item.appid, item.contextid));

            if(success)
                myOfferedItems[slot] = item.assetid;
            
            return success;
        }

        /// <summary>
        /// Adds a single item by its Defindex.
        /// </summary>
        /// <returns>
        /// <c>true</c> if an item was found with the corresponding
        /// defindex, <c>false</c> otherwise.
        /// </returns>
        public bool AddItemByDefindex (int defindex)
        {
            List<Inventory.Item> items = MyInventory.GetItemsByDefindex (defindex);
            foreach (Inventory.Item item in items)
            {
                if (item != null && !myOfferedItems.ContainsValue(item.Id) && !item.IsNotTradeable)
                {
                    return AddItem (item.Id);
                }
            }
            return false;
        }

        /// <summary>
        /// Adds an entire set of items by Defindex to each successive
        /// slot in the trade.
        /// </summary>
        /// <param name="defindex">The defindex. (ex. 5022 = crates)</param>
        /// <param name="numToAdd">The upper limit on amount of items to add. <c>0</c> to add all items.</param>
        /// <returns>Number of items added.</returns>
        public uint AddAllItemsByDefindex (int defindex, uint numToAdd = 0)
        {
            List<Inventory.Item> items = MyInventory.GetItemsByDefindex (defindex);

            uint added = 0;

            foreach (Inventory.Item item in items)
            {
                if (item != null && !myOfferedItems.ContainsValue(item.Id) && !item.IsNotTradeable)
                {
                    bool success = AddItem (item.Id);

                    if (success)
                        added++;

                    if (numToAdd > 0 && added >= numToAdd)
                        return added;
                }
            }

            return added;
        }


        public bool RemoveItem(TradeUserAssets item)
        {
            return RemoveItem(item.assetid, item.appid, item.contextid);
        }

        /// <summary>
        /// Removes an item by its itemid.
        /// </summary>
        /// <returns><c>false</c> the item was not found in the trade.</returns>
        public bool RemoveItem (ulong itemid, int appid = 440, long contextid = 2)
        {
            int? slot = GetItemSlot (itemid);
            if (!slot.HasValue)
                return false;

            bool success = RetryWebRequest(() => session.RemoveItemWebCmd(itemid, slot.Value, appid, contextid));

            if(success)
                myOfferedItems.Remove (slot.Value);

            return success;
        }

        /// <summary>
        /// Removes an item with the given Defindex from the trade.
        /// </summary>
        /// <returns>
        /// Returns <c>true</c> if it found a corresponding item; <c>false</c> otherwise.
        /// </returns>
        public bool RemoveItemByDefindex (int defindex)
        {
            foreach (ulong id in myOfferedItems.Values)
            {
                Inventory.Item item = MyInventory.GetItem (id);
                if (item != null && item.Defindex == defindex)
                {
                    return RemoveItem (item.Id);
                }
            }
            return false;
        }

        /// <summary>
        /// Removes an entire set of items by Defindex.
        /// </summary>
        /// <param name="defindex">The defindex. (ex. 5022 = crates)</param>
        /// <param name="numToRemove">The upper limit on amount of items to remove. <c>0</c> to remove all items.</param>
        /// <returns>Number of items removed.</returns>
        public uint RemoveAllItemsByDefindex (int defindex, uint numToRemove = 0)
        {
            List<Inventory.Item> items = MyInventory.GetItemsByDefindex (defindex);

            uint removed = 0;

            foreach (Inventory.Item item in items)
            {
                if (item != null && myOfferedItems.ContainsValue (item.Id))
                {
                    bool success = RemoveItem (item.Id);

                    if (success)
                        removed++;

                    if (numToRemove > 0 && removed >= numToRemove)
                        return removed;
                }
            }

            return removed;
        }

        /// <summary>
        /// Removes all offered items from the trade.
        /// </summary>
        /// <returns>Number of items removed.</returns>
        public uint RemoveAllItems()
        {
            uint numRemoved = 0;

            foreach(var id in myOfferedItems.Values.ToList())
            {
                Inventory.Item item = MyInventory.GetItem(id);

                if(item != null)
                {
                    bool wasRemoved = RemoveItem(item.Id);

                    if(wasRemoved)
                        numRemoved++;
                }
            }

            return numRemoved;
        }

        /// <summary>
        /// Sends a message to the user over the trade chat.
        /// </summary>
        public bool SendMessage (string msg)
        {
            return RetryWebRequest(() => session.SendMessageWebCmd(msg));
        }

        /// <summary>
        /// Sets the bot to a ready status.
        /// </summary>
        public bool SetReady (bool ready)
        {
            //If the bot calls SetReady(false) and the call fails, we still want meIsReady to be
            //set to false.  Otherwise, if the call to SetReady() was a result of a callback
            //from Trade.Poll() inside of the OnTradeAccept() handler, the OnTradeAccept()
            //handler might think the bot is ready, when really it's not!
            if(!ready)
                MeIsReady = false;

            // testing
            ValidateLocalTradeItems ();

            return RetryWebRequest(() => session.SetReadyWebCmd(ready));
        }

        /// <summary>
        /// Accepts the trade from the user.  Returns a deserialized
        /// JSON object.
        /// </summary>
        public bool AcceptTrade ()
        {
            ValidateLocalTradeItems ();

            return RetryWebRequest(session.AcceptTradeWebCmd);
        }

        /// <summary>
        /// Calls the given function multiple times, until we get a non-null/non-false/non-zero result, or we've made at least
        /// WEB_REQUEST_MAX_RETRIES attempts (with WEB_REQUEST_TIME_BETWEEN_RETRIES_MS between attempts)
        /// </summary>
        /// <returns>The result of the function if it succeeded, or default(T) (null/false/0) otherwise</returns>
        private T RetryWebRequest<T>(Func<T> webEvent)
        {
            for(int i = 0; i < WEB_REQUEST_MAX_RETRIES; i++)
            {
                //Don't make any more requests if the trade has ended!
                if(HasTradeCompletedOk || OtherUserCancelled)
                    return default(T);

                T result = webEvent();
                if(!EqualityComparer<T>.Default.Equals(result, default(T)))
                    return result;
                if(i != WEB_REQUEST_MAX_RETRIES)
                {
                    //This will cause the bot to stop responding while we wait between web requests.  ...Is this really what we want?
                    Thread.Sleep(WEB_REQUEST_TIME_BETWEEN_RETRIES_MS);
                }
            }
            return default(T);
        }

        /// <summary>
        /// This updates the trade.  This is called at an interval of a
        /// default of 800ms, not including the execution time of the
        /// method itself.
        /// </summary>
        /// <returns><c>true</c> if the other trade partner performed an action; otherwise <c>false</c>.</returns>
        public bool Poll ()
        {
            bool otherDidSomething = false;

            if (!TradeStarted)
            {
                TradeStarted = true;

                // since there is no feedback to let us know that the trade
                // is fully initialized we assume that it is when we start polling.
                if (OnAfterInit != null)
                    OnAfterInit ();
            }

            TradeStatus status = RetryWebRequest(session.GetStatus);

            if (status == null)
                return false;

            switch (status.trade_status)
            {
                // Nothing happened. i.e. trade hasn't closed yet.
                case 0:
                    break;

                // Successful trade
                case 1:
                    HasTradeCompletedOk = true;
                    return false;

                // All other known values (3, 4) correspond to trades closing.
                default:
                    if (OnError != null)
                    {
                        OnError("Trade was closed by other user. Trade status: " + status.trade_status);
                    }
                    OtherUserCancelled = true;
                    return false;
            }

            if (status.newversion)
            {
                // handle item adding and removing
                session.Version = status.version;

                HandleTradeVersionChange(status);
                return true;
            }
            else if (status.version > session.Version)
            {
                // oh crap! we missed a version update abort so we don't get 
                // scammed. if we could get what steam thinks what's in the 
                // trade then this wouldn't be an issue. but we can only get 
                // that when we see newversion == true
                throw new TradeException("The trade version does not match. Aborting.");
            }

            var events = status.GetAllEvents();

            foreach (var tradeEvent in events)
            {
                if (eventList.Contains(tradeEvent))
                    continue;

                //add event to processed list, as we are taking care of this event now
                eventList.Add(tradeEvent);

                bool isBot = tradeEvent.steamid == MySteamId.ConvertToUInt64().ToString();

                // dont process if this is something the bot did
                if (isBot)
                    continue;

                otherDidSomething = true;

                switch ((TradeEventType) tradeEvent.action)
                {
                    case TradeEventType.ItemAdded:
                        FireOnUserAddItem(tradeEvent);
                        break;
                    case TradeEventType.ItemRemoved:
                        FireOnUserRemoveItem(tradeEvent);
                        break;
                    case TradeEventType.UserSetReady:
                        OtherIsReady = true;
                        OnUserSetReady(true);
                        break;
                    case TradeEventType.UserSetUnReady:
                        OtherIsReady = false;
                        OnUserSetReady(false);
                        break;
                    case TradeEventType.UserAccept:
                        OnUserAccept();
                        break;
                    case TradeEventType.UserChat:
                        OnMessage(tradeEvent.text);
                        break;
                    default:
                        // Todo: add an OnWarning or similar event
                        if (OnError != null)
                            OnError("Unknown Event ID: " + tradeEvent.action);
                        break;
                }
            }

            // Update Local Variables
            if (status.them != null)
            {
                OtherIsReady = status.them.ready == 1;
                MeIsReady = status.me.ready == 1;
            }

            if (status.logpos != 0)
            {
                session.LogPos = status.logpos;
            }

            return otherDidSomething;
        }

        private void HandleTradeVersionChange(TradeStatus status)
        {
            CopyNewAssets(OtherOfferedItems, status.them.GetAssets());

            CopyNewAssets(steamMyOfferedItems, status.me.GetAssets());
        }

        private void CopyNewAssets(List<ulong> dest, IEnumerable<TradeUserAssets> assetList)
        {
            if (assetList == null) 
                return;

            dest.Clear();
            dest.AddRange(assetList.Select(asset => asset.assetid));
        }

        /// <summary>
        /// Gets an item from a TradeEvent, and passes it into the UserHandler's implemented OnUserAddItem([...]) routine.
        /// Passes in null items if something went wrong.
        /// </summary>
        /// <param name="tradeEvent">TradeEvent to get item from</param>
        /// <returns></returns>
        private void FireOnUserAddItem(TradeEvent tradeEvent)
        {
            ulong itemID = tradeEvent.assetid;

            if (OtherInventory != null)
            {
                Inventory.Item item = OtherInventory.GetItem(itemID);
                if (item != null)
                {
                    Schema.Item schemaItem = CurrentSchema.GetItem(item.Defindex);
                    if (schemaItem == null)
                    {
                        Console.WriteLine("User added an unknown item to the trade.");
                    }

                    OnUserAddItem(schemaItem, item);
                }
                else
                {
                    item = new Inventory.Item
                    {
                        Id=itemID,
                        AppId=tradeEvent.appid,
                        ContextId=tradeEvent.contextid
                    };
                    //Console.WriteLine("User added a non TF2 item to the trade.");
                    OnUserAddItem(null, item);
                }
            }
            else
            {
                var schemaItem = GetItemFromPrivateBp(tradeEvent, itemID);
                if (schemaItem == null)
                {
                    Console.WriteLine("User added an unknown item to the trade.");
                }

                OnUserAddItem(schemaItem, null);
                // todo: figure out what to send in with Inventory item.....
            }
        }

        private Schema.Item GetItemFromPrivateBp(TradeEvent tradeEvent, ulong itemID)
        {
            if (OtherPrivateInventory == null)
            {
                // get the foreign inventory
                var f = session.GetForiegnInventory(OtherSID, tradeEvent.contextid, tradeEvent.appid);
                OtherPrivateInventory = new ForeignInventory(f);
            }

            ushort defindex = OtherPrivateInventory.GetDefIndex(itemID);

            Schema.Item schemaItem = CurrentSchema.GetItem(defindex);
            return schemaItem;
        }

        /// <summary>
        /// Gets an item from a TradeEvent, and passes it into the UserHandler's implemented OnUserRemoveItem([...]) routine.
        /// Passes in null items if something went wrong.
        /// </summary>
        /// <param name="tradeEvent">TradeEvent to get item from</param>
        /// <returns></returns>
        private void FireOnUserRemoveItem(TradeEvent tradeEvent)
        {
            ulong itemID = tradeEvent.assetid;

            if (OtherInventory != null)
            {
                Inventory.Item item = OtherInventory.GetItem(itemID);
                if (item != null)
                {
                    Schema.Item schemaItem = CurrentSchema.GetItem(item.Defindex);
                    if (schemaItem == null)
                    {
                        // TODO: Add log (counldn't find item in CurrentSchema)
                    }

                    OnUserRemoveItem(schemaItem, item);
                }
                else
                {
                    // TODO: Log this (Couldn't find item in user's inventory can't find item in CurrentSchema
                    item = new Inventory.Item()
                    {
                        Id = itemID,
                        AppId = tradeEvent.appid,
                        ContextId = tradeEvent.contextid
                    };
                    OnUserRemoveItem(null, item);
                }
            }
            else
            {
                var schemaItem = GetItemFromPrivateBp(tradeEvent, itemID);
                if (schemaItem == null)
                {
                    // TODO: Add log (counldn't find item in CurrentSchema)
                }

                OnUserRemoveItem(schemaItem, null);
            }
        }

        internal void FireOnCloseEvent()
        {
            var onCloseEvent = OnClose;

            if (onCloseEvent != null)
                onCloseEvent();
        }

        private int NextTradeSlot()
        {
            int slot = 0;
            while (myOfferedItems.ContainsKey (slot))
            {
                slot++;
            }
            return slot;
        }

        private int? GetItemSlot(ulong itemid)
        {
            foreach (int slot in myOfferedItems.Keys)
            {
                if (myOfferedItems [slot] == itemid)
                {
                    return slot;
                }
            }
            return null;
        }

        private void ValidateLocalTradeItems ()
        {
            if (myOfferedItems.Count != steamMyOfferedItems.Count)
            {
                throw new TradeException ("Error validating local copy of items in the trade: Count mismatch");
            }

            if (myOfferedItems.Values.Any(id => !steamMyOfferedItems.Contains(id)))
            {
                throw new TradeException ("Error validating local copy of items in the trade: Item was not in the Steam Copy.");
            }
        }
    }
}

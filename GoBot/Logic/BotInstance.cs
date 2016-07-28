﻿

using GoBot.UserLogger;
using GoBot.Utils;
using POGOProtos.Data;
using POGOProtos.Enums;
using POGOProtos.Inventory;
using POGOProtos.Inventory.Item;
using POGOProtos.Map.Fort;
using POGOProtos.Map.Pokemon;
using POGOProtos.Networking.Responses;
using PokemonGo.RocketAPI;
using PokemonGo.RocketAPI.Enums;
using PokemonGo.RocketAPI.Exceptions;
using PokemonGo.RocketAPI.Extensions;

using PokemonGo.RocketAPI.Login;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GoBot.Logic
{
    public class BotInstance
    {
        public readonly Client _client;
        public readonly ISettings _clientSettings;
        public readonly Inventory _inventory;
        public readonly Navigation _navigation;

        public Statistics _stats;

        public List<PokemonId> TransferList;
        public List<PokemonId> EvolveList;
        public List<PokemonId> CatchList;
        public List<PokemonId> BerryList;

        public List<FortData> VisitedForts = new List<FortData>();

        public bool running = false;

        public Random rand = new Random();

        public BotInstance(ISettings clientSettings)
        {

            _clientSettings = clientSettings;
            _client = new Client(_clientSettings);
            _inventory = new Inventory(_client);
            _navigation = new Navigation(_client);
            _stats = new Statistics();
            Events.OnStepWalked += Events_OnStepWalked;
        }

        private async void Events_OnStepWalked(object sender, StepWalkedArgs e)
        {
            if (!UserSettings.CatchPokemonOnWalk)
            {
                Events.StepWalkedReset.Set();
                return;
            }

            await ExecuteCatchAllNearbyPokemons(true);
            if (_client.CurrentLatitude != e.curLocation.Latitude && _client.CurrentLongitude != e.curLocation.Longitude)
            {
                Logger.Write("Walking back to initial route...");
                // only if they have moved
                if (UserSettings.TeleportToPokemonOnWalk)
                {
                    var update = await _client.Player.UpdatePlayerLocation(e.curLocation.Latitude, e.curLocation.Longitude, UserSettings.Altitude);
                }
                else
                {
                    var update = await _navigation.HumanLikeWalking(e.curLocation, UserSettings.WalkingSpeed, true, true);
                }
            }
            Events.StepWalkedReset.Set();

        }

        public void Stop()
        {
            running = false;
            Logger.Write("Stop was called!");
        }
        public async Task Execute()
        {
            Logger.Write($"Starting Execute on login server: {_clientSettings.AuthType}", LogLevel.Info, ConsoleColor.Magenta);
            running = true;
            while (running)
            {
                try
                {
                    /*if (await Login())
                    {
                        running = true;

                        await PostLoginExecute();

                        running = true;
                    }
                    else
                    {
                        running = false;
                        break;
                    }*/
                  

                    await PostLoginExecute();
                }
                catch (Exception ex)
                {
                    Logger.Write($"Execute Exception: {ex}", LogLevel.Info, ConsoleColor.Red);
                    
                }
                Logger.Write($"Looping Execute Again", LogLevel.Info);
                await T.Delay(rand.Next(8000, 15000));
            }
        }

        private void Login_GoogleDeviceCodeEvent(string code, string uri)
        {

            Logger.Write($"Your Google Device Code is {code} enter it at {uri}", LogLevel.Info, ConsoleColor.White);

            Logger.Write("Once entered, please wait for the bot to start...", LogLevel.Info, ConsoleColor.White);
           

        }

        public async Task<bool> Login()
        {
            try
            {
                if (_clientSettings.AuthType == AuthType.Ptc)
                    await _client.Login.DoPtcLogin(UserSettings.Username, UserSettings.Password);
                else if (_clientSettings.AuthType == AuthType.Google)
                {
                    await _client.Login.DoGoogleLogin(UserSettings.Username, UserSettings.Password);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Write($"Login Exception: {ex}", LogLevel.Info, ConsoleColor.Red);
            }
            return false;
        }
        public async Task PostLoginExecute()
        {
            while (running)
            {
                // login... messy
                if (!await Login())
                {
                    Logger.Write($"Invalid email/username or password for Google/Ptc Authentication!");
                    running = false;
                    break;
                }

                try
                {
                    if (!UserSettings.CatchPokemon && !UserSettings.GetForts)
                    {
                        // idle instead and clean/evolve/whatever.
                        // set server is done in ptclogin/googlelogin
                        await EvolveAllPokemonWithEnoughCandy();
                        await RecycleItems();
                        await TransferDuplicatePokemon(UserSettings.KeepCP, false);
                    }
                    else
                    {

                        await EvolveAllPokemonWithEnoughCandy();
                        await RecycleItems();
                        await TransferDuplicatePokemon(UserSettings.KeepCP, false);


                        await ExecuteFarmingForts(UserSettings.CatchPokemon);
                    }

                }
                catch (AccessTokenExpiredException)
                {
                    Logger.Write("Access token was expired", LogLevel.Error, ConsoleColor.Red);
                    throw;
                }
                catch (Exception ex)
                {
                    //if ()
                    Logger.Write($"Exception (PostLogin): {ex}", LogLevel.Error, ConsoleColor.Red);
                }

                await T.Delay(rand.Next(8000, 15000));
                Logger.Write($"Looping PostLogin Again - Idle: {!UserSettings.CatchPokemon && !UserSettings.GetForts}", LogLevel.Info);
                Logger.Write($"If this is happening a lot it means the Pokemon servers are unstable, do not spam issues on git with this! We can't fix it!", LogLevel.Info);
            }
            Logger.Write($"We're out of the loop now, Running is {running}");
            // walk home
            try
            {
                await WalkToStart();
            }
            catch (Exception ex)
            {
                Logger.Write($"Exception: {ex}", LogLevel.Error, ConsoleColor.Red);
            }
        }

        public async Task WalkToStart()
        {
            Logger.Write("Stopping and walking to start point...");
            var update = await _navigation.DirectionalWalking(new Navigation.Location(_clientSettings.DefaultLatitude, _clientSettings.DefaultLongitude), UserSettings.WalkingSpeed);//await _navigation.HumanLikeWalking(new Navigation.Location(_clientSettings.DefaultLatitude, _clientSettings.DefaultLongitude), UserSettings.WalkingSpeed);
            Logger.Write("Bot has been stopped and has reached the start point used. You may now safely exit.");
        }
        public async Task ExecuteFarmingForts(bool getPokes)
        {
            var mapObjects = await _client.Map.GetMapObjects();

            var pokeStops = mapObjects.MapCells.SelectMany(i => i.Forts).Where(i => i.Type == FortType.Checkpoint && i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime()).OrderBy(i => LocationUtils.CalculateDistanceInMeters(new Navigation.Location(_client.CurrentLatitude, _client.CurrentLongitude), new Navigation.Location(i.Latitude, i.Longitude)));

            if (UserSettings.NoDupeForts)
                pokeStops = pokeStops.Where(i => !VisitedForts.Contains(i)).OrderBy(i => LocationUtils.CalculateDistanceInMeters(new Navigation.Location(_client.CurrentLatitude, _client.CurrentLongitude), new Navigation.Location(i.Latitude, i.Longitude)));

            if (pokeStops.ToList().Count == 0)
            {
                VisitedForts.Clear();
            }

            
            Logger.Write($"Farming {pokeStops.ToList().Count} PokeStops... {running}", LogLevel.Info, ConsoleColor.Cyan);
            foreach (var pokeStop in pokeStops)
            {

                if (!running)
                    break;
                /*if (UserSettings.Teleport)
                {
                    // You'll be banned, I warned you...
                    var dist = LocationUtils.CalculateDistanceInMeters(new Navigation.Location(_client.CurrentLatitude, _client.CurrentLongitude), new Navigation.Location(pokemon.Latitude, pokemon.Longitude));
                    var update = await _client.Player.UpdatePlayerLocation(pokeStop.Latitude, pokeStop.Longitude, UserSettings.Altitude);
                }
                else
                {*/
                    var update =
                        await _navigation.DirectionalWalking(new Navigation.Location(pokeStop.Latitude, pokeStop.Longitude), UserSettings.WalkingSpeed);
                //}

                if (UserSettings.GetForts)
                {
                    //var fortInfo = await client.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                    var fortSearch = await _client.Fort.SearchFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);

                    VisitedForts.Add(pokeStop);

                    _stats.addExperience(fortSearch.ExperienceAwarded);
                    _stats.updateConsoleTitle(_inventory);



                    await Events.FortFarmed(fortSearch, pokeStop);


                    Logger.Write($"Farmed XP: {fortSearch.ExperienceAwarded}, Gems: { fortSearch.GemsAwarded}, Eggs: {fortSearch.PokemonDataEgg} Items: {StringUtils.GetSummedFriendlyNameOfItemAwardList(fortSearch.ItemsAwarded)}", LogLevel.Info, ConsoleColor.Cyan);
                    await T.Delay(rand.Next(3000, 6000));
                }

                var profile = await _client.Player.GetPlayer();
                _stats.getStardust(profile.PlayerData.Currencies.ToArray()[1].Amount);
                _stats.updateConsoleTitle(_inventory);

                if (getPokes)
                   await ExecuteCatchAllNearbyPokemons();

                await T.Delay(15000);
            }
        }

        private async Task ExecuteCatchAllNearbyPokemons(bool fromWalking = false)
        {
            var mapObjects = await _client.Map.GetMapObjects();

            var catchPokemons = mapObjects.MapCells.SelectMany(i => i.CatchablePokemons).OrderBy(i => LocationUtils.CalculateDistanceInMeters(new Navigation.Location(_client.CurrentLatitude, _client.CurrentLongitude), new Navigation.Location(i.Latitude, i.Longitude)));
            var nearPokemons = mapObjects.MapCells.SelectMany(i => i.NearbyPokemons).OrderBy(i => i.DistanceInMeters);
            var wildPokemons = mapObjects.MapCells.SelectMany(i => i.WildPokemons).OrderBy(i => LocationUtils.CalculateDistanceInMeters(new Navigation.Location(_client.CurrentLatitude, _client.CurrentLongitude), new Navigation.Location(i.Latitude, i.Longitude)));

            Logger.Write($"Catching {catchPokemons.ToList().Count} nearby Pokemon", LogLevel.Info, ConsoleColor.Magenta);

            foreach (var pokemon in catchPokemons)
            {
                if (!running)
                    break;
                try
                {
                    // we'll teleport if we're on the walk? I don't think that'd be wise
                    var dist = LocationUtils.CalculateDistanceInMeters(new Navigation.Location(_client.CurrentLatitude, _client.CurrentLongitude), new Navigation.Location(pokemon.Latitude, pokemon.Longitude));
                    if (UserSettings.Teleport || (fromWalking && UserSettings.TeleportToPokemonOnWalk))
                    {
                       
                        if (dist > UserSettings.CatchWalkRadius && fromWalking)
                        {
                            Logger.Write($"Did not catch pokemon due to it being >{UserSettings.CatchWalkRadius} meters from the pokestop route!");
                            continue;
                        }
                        await Task.Delay(dist > 100 ? 5000 : 500);
                        var update = await _client.Player.UpdatePlayerLocation(pokemon.Latitude, pokemon.Longitude, UserSettings.Altitude);
                    }
                    else
                    {
                        if (dist > UserSettings.CatchWalkRadius && fromWalking)
                        {
                            Logger.Write($"Did not catch pokemon due to it being >{UserSettings.CatchWalkRadius} meters from the pokestop route!");
                            continue;
                        }
                        var update = await _navigation.DirectionalWalking(new Navigation.Location(pokemon.Latitude, pokemon.Longitude), UserSettings.WalkingSpeed, true);
                    }

                    var encounter = await _client.Encounter.EncounterPokemon(pokemon.EncounterId, pokemon.SpawnPointId);

                    if (encounter.Status == EncounterResponse.Types.Status.EncounterSuccess)
                    {
                        var pokeId = encounter?.WildPokemon?.PokemonData.PokemonId;
                        await CatchEncounter(encounter, pokemon);
                    }


                    await T.Delay(rand.Next(15000, 30000));
                }
                catch (Exception ex)
                {
                    Logger.Write($"Exception Catch MapPokemon: {ex}", LogLevel.Error, ConsoleColor.Red);
                }
            }
        }

        
        private async Task CatchEncounter(EncounterResponse encounter, MapPokemon pokemon)
        {

            CatchPokemonResponse caughtPokemonResponse;
            do
            {
                try
                {
                    PokemonData pokeData = encounter?.WildPokemon?.PokemonData;

                    if (encounter?.CaptureProbability != null && encounter?.CaptureProbability.CaptureProbability_.First() < (UserSettings.BerryProbability / 100))
                    {
                        if (pokeData != null)
                        {
                            if (BerryList.Contains(pokeData.PokemonId))
                            {
                                await UseBerry(pokemon.EncounterId, pokemon.SpawnPointId);
                            }
                        }


                    }

                    var pokeball = await GetBestBall(encounter?.WildPokemon);

                    if (pokeball == ItemId.ItemUnknown)
                    {
                        Logger.Write("No Pokeballs to use! STOPPING BOT!", LogLevel.Error, ConsoleColor.Red);
                        Stop();
                        return;
                    }
                    caughtPokemonResponse = await _client.Encounter.CatchPokemon(pokemon.EncounterId, pokemon.SpawnPointId, pokeball);

                    Logger.Write(caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess ? $"We caught a {pokemon.PokemonId} with CP {encounter?.WildPokemon?.PokemonData?.Cp} ({CalculatePokemonPerfection(encounter?.WildPokemon?.PokemonData).ToString("0.00")}% perfection) using a {pokeball}" : $"{pokemon.PokemonId} with CP {encounter?.WildPokemon?.PokemonData?.Cp} got away while using a {pokeball}..", LogLevel.Info, ConsoleColor.Green);

                    if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess)
                    {
                        pokeData = encounter?.WildPokemon?.PokemonData;
                       
                        foreach (int xp in caughtPokemonResponse.CaptureAward.Xp)
                            _stats.addExperience(xp);

                        var profile = await _client.Player.GetPlayer();
                        _stats.getStardust(profile.PlayerData.Currencies.ToArray()[1].Amount);

                        _stats.increasePokemons();
                        _stats.updateConsoleTitle(_inventory);
                        await Events.PokemonCaught(encounter?.WildPokemon?.PokemonData, caughtPokemonResponse.CapturedPokemonId);
                    }

                    await T.Delay(rand.Next(1500, 3000));
                }
                catch (Exception ex)
                {
                    Logger.Write($"Exception in Encounter: {ex}", LogLevel.Error, ConsoleColor.Red);
                    break;
                }
            }
            while (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchMissed);
        }

        private async Task<ItemId> GetBestBall(WildPokemon pokemon)
        {
            var pokemonCp = pokemon?.PokemonData?.Cp;

            var items = await _inventory.GetItems();
            var balls = items.Where(i => (i.ItemId == ItemId.ItemPokeBall
                                      || i.ItemId == ItemId.ItemGreatBall
                                      || i.ItemId == ItemId.ItemUltraBall
                                      || i.ItemId == ItemId.ItemMasterBall) && i.Count > 0).GroupBy(i => (i.ItemId)).ToList();
            if (balls.Count == 0) return ItemId.ItemUnknown;

            var pokeBalls = balls.Any(g => g.Key == ItemId.ItemPokeBall);
            var greatBalls = balls.Any(g => g.Key == ItemId.ItemGreatBall);
            var ultraBalls = balls.Any(g => g.Key == ItemId.ItemUltraBall);
            var masterBalls = balls.Any(g => g.Key == ItemId.ItemMasterBall);

            if (masterBalls && pokemonCp >= 2000)
                return ItemId.ItemMasterBall;
            else if (ultraBalls && pokemonCp >= 2000)
                return ItemId.ItemUltraBall;
            else if (greatBalls && pokemonCp >= 2000)
                return ItemId.ItemGreatBall;

            if (ultraBalls && pokemonCp >= 1000)
                return ItemId.ItemUltraBall;
            else if (greatBalls && pokemonCp >= 1000)
                return ItemId.ItemGreatBall;

            if (greatBalls && pokemonCp >= 500)
                return ItemId.ItemGreatBall;

            return balls.OrderBy(g => g.Key).First().Key;
        }
        private async Task<ItemId> GetBestBerry(WildPokemon pokemon)
        {
            var pokemonCp = pokemon?.PokemonData?.Cp;

            var items = await _inventory.GetItems();
            var berries = items.Where(i => i.ItemId == ItemId.ItemRazzBerry
                                        || i.ItemId == ItemId.ItemBlukBerry
                                        || i.ItemId == ItemId.ItemNanabBerry
                                        || i.ItemId == ItemId.ItemWeparBerry
                                        || i.ItemId == ItemId.ItemPinapBerry).GroupBy(i => (i.ItemId)).ToList();
            if (berries.Count == 0 || pokemonCp <= 350) return ItemId.ItemUnknown;

            var razzBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemRazzBerry);
            var blukBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemBlukBerry);
            var nanabBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemNanabBerry);
            var weparBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemWeparBerry);
            var pinapBerryCount = await _inventory.GetItemAmountByType(ItemId.ItemPinapBerry);

            if (pinapBerryCount > 0 && pokemonCp >= 2000)
                return ItemId.ItemPinapBerry;
            else if (weparBerryCount > 0 && pokemonCp >= 2000)
                return ItemId.ItemWeparBerry;
            else if (nanabBerryCount > 0 && pokemonCp >= 2000)
                return ItemId.ItemNanabBerry;
            else if (nanabBerryCount > 0 && pokemonCp >= 2000)
                return ItemId.ItemBlukBerry;

            if (weparBerryCount > 0 && pokemonCp >= 1500)
                return ItemId.ItemWeparBerry;
            else if (nanabBerryCount > 0 && pokemonCp >= 1500)
                return ItemId.ItemNanabBerry;
            else if (blukBerryCount > 0 && pokemonCp >= 1500)
                return ItemId.ItemBlukBerry;

            if (nanabBerryCount > 0 && pokemonCp >= 1000)
                return ItemId.ItemNanabBerry;
            else if (blukBerryCount > 0 && pokemonCp >= 1000)
                return ItemId.ItemBlukBerry;

            if (blukBerryCount > 0 && pokemonCp >= 500)
                return ItemId.ItemBlukBerry;

            return berries.OrderBy(g => g.Key).First().Key;
        }
        public async Task UseBerry(ulong encounterId, string spawnPointId)
        {
            if (!UserSettings.UseBerries)
                return;

            var inventoryBalls = await _inventory.GetItems();
            var berries = inventoryBalls.Where(p => (ItemId)p.ItemId == ItemId.ItemRazzBerry);
            var berry = berries.FirstOrDefault();

            if (berry == null)
                return;

            var useRaspberry = await _client.Encounter.UseCaptureItem(encounterId, ItemId.ItemRazzBerry, spawnPointId);
            Logger.Write($"Use Rasperry. Remaining: {berry.Count}", LogLevel.Info);
            await T.Delay(rand.Next(4000, 8000));
        }
        public static float CalculatePokemonPerfection(PokemonData poke)
        {
            return ((float)(poke.IndividualAttack * 2 + poke.IndividualDefense + poke.IndividualStamina) / (4.0f * 15.0f)) * 100.0f;
        }
        public async Task EvolveAllPokemonWithEnoughCandy()
        {
            var pokemonToEvolve = await _inventory.GetPokemonToEvolve();
            Logger.Write($"Sorting through {pokemonToEvolve.ToList().Count} pokemon to evolve...", LogLevel.Info, ConsoleColor.DarkGreen);

            foreach (var pokemon in pokemonToEvolve)
            {
                if (!EvolveList.Contains(pokemon.PokemonId))
                    continue;

                if (pokemon.Cp < UserSettings.EvolveOverCP && CalculatePokemonPerfection(pokemon) < UserSettings.EvolveOverIV)
                {
                    Logger.Write($"Did not Evolve {pokemon.PokemonId} ({pokemon.Cp} cp, {CalculatePokemonPerfection(pokemon).ToString("0.00")}%) (Under Requirement) ", LogLevel.Info);

                    continue;
                }

                var evolvePokemonOutProto = await _client.Inventory.EvolvePokemon((ulong)pokemon.Id);
                _stats.increasePokemonsTransfered();
                _stats.updateConsoleTitle(_inventory);
                if (evolvePokemonOutProto.Result == EvolvePokemonResponse.Types.Result.Success)
                    Logger.Write($"Evolved {pokemon.PokemonId} successfully for {evolvePokemonOutProto.ExperienceAwarded}xp", LogLevel.Info, ConsoleColor.DarkGreen);
                else
                    Logger.Write($"Failed to evolve {pokemon.PokemonId}. EvolvePokemonOutProto.Result was {evolvePokemonOutProto.Result}, stopping evolving {pokemon.PokemonId}", LogLevel.Info);


                await T.Delay(rand.Next(3000, 5000));
            }
        }

        public async Task TransferDuplicatePokemon(int keepCp, bool keepPokemonsThatCanEvolve = false)
        {

            var duplicatePokemons = await _inventory.GetDuplicatePokemonToTransfer(keepCp, keepPokemonsThatCanEvolve);

            Logger.Write($"Sorting through {duplicatePokemons.ToList().Count} pokemon to transfer duplicates...", LogLevel.Info, ConsoleColor.Yellow);

            foreach (var duplicatePokemon in duplicatePokemons)
            {
                // stop transfer of pokemon that are not on transfer list
                if (!TransferList.Contains(duplicatePokemon.PokemonId))
                    continue;

                if (duplicatePokemon.Cp > UserSettings.KeepCP || CalculatePokemonPerfection(duplicatePokemon) > UserSettings.KeepIV)
                {
                    Logger.Write($"Did not Transfer {duplicatePokemon.PokemonId} ({duplicatePokemon.Cp} cp, {CalculatePokemonPerfection(duplicatePokemon).ToString("0.00")}%) (Over Requirement) ", LogLevel.Info);
                    continue;
                }

                var transfer = await _client.Inventory.TransferPokemon(duplicatePokemon.Id);
                _stats.increasePokemonsTransfered();
                _stats.updateConsoleTitle(_inventory);
                Logger.Write($"Transferred {duplicatePokemon.PokemonId} with {duplicatePokemon.Cp} CP", LogLevel.Info, ConsoleColor.Yellow);
                await T.Delay(rand.Next(3000, 6000));
            }
        }

        public async Task TransferPokemonFromList()
        {
            var pokemons = await _inventory.GetPokemons();
            var pokemonList = pokemons as IList<PokemonData> ?? pokemons.ToList();

            // UNTESTED
            foreach (var pokemon in pokemonList.Where(x => x.Favorite == 0).OrderByDescending(i=> i.Cp).ThenBy(i => i.StaminaMax).Skip(UserSettings.TopX))
            {
                if (!TransferList.Contains(pokemon.PokemonId))
                    continue;
                if (pokemon.Cp > UserSettings.KeepCP || CalculatePokemonPerfection(pokemon) > UserSettings.KeepIV)
                {
                    Logger.Write($"Did not Transfer {pokemon.PokemonId} ({pokemon.Cp} cp, {CalculatePokemonPerfection(pokemon).ToString("0.00")}%) (Over Requirement) ", LogLevel.Info);
                    continue;
                }
                var transfer = await _client.Inventory.TransferPokemon(pokemon.Id);
                _stats.increasePokemonsTransfered();
                _stats.updateConsoleTitle(_inventory);
                Logger.Write($"Transferred {pokemon.PokemonId} with {pokemon.Cp} CP", LogLevel.Info, ConsoleColor.Yellow);
                await T.Delay(rand.Next(3000, 6000));
            }
        }

        public async Task OverrideTransfer(int keepCp, int keepIv)
        {
            var pokemons = await _inventory.GetPokemons();
            var pokemonList = pokemons as IList<PokemonData> ?? pokemons.ToList();
            Logger.Write($"Forcibly (override) sorting transfer of {pokemonList.Count} pokemon(s)", LogLevel.Info, ConsoleColor.Yellow);
            // UNTESTED
            foreach (var pokemon in pokemonList.Where(x => x.Favorite == 0).OrderByDescending(i => i.Cp).ThenBy(i => i.StaminaMax).Skip(UserSettings.TopX))
            {
               // if (!TransferList.Contains(pokemon.PokemonId))
               //     continue;
                if (pokemon.Cp > keepCp || CalculatePokemonPerfection(pokemon) > keepIv)
                {
                    Logger.Write($"Did not Transfer {pokemon.PokemonId} ({pokemon.Cp} cp, {CalculatePokemonPerfection(pokemon).ToString("0.00")}%) (Over Requirement) ", LogLevel.Info);
                    continue;
                }
                var transfer = await _client.Inventory.TransferPokemon(pokemon.Id);
                _stats.increasePokemonsTransfered();
                _stats.updateConsoleTitle(_inventory);
                Logger.Write($"Transferred {pokemon.PokemonId} with {pokemon.Cp} CP ({CalculatePokemonPerfection(pokemon).ToString("0.00")}%)", LogLevel.Info, ConsoleColor.Yellow);
                await T.Delay(rand.Next(3000, 6000));
            }
        }

        public async Task OverrideEvolve(int keepCp, int keepIv)
        {
            var pokemonToEvolve = await _inventory.GetPokemonToEvolve();
            Logger.Write($"Forcibly (override) sorting evolution of {pokemonToEvolve.ToList().Count} pokemon(s)", LogLevel.Info, ConsoleColor.DarkGreen);

            foreach (var pokemon in pokemonToEvolve)
            {
                //if (!EvolveList.Contains(pokemon.PokemonId))
                //    continue;

                if (pokemon.Cp < keepCp || CalculatePokemonPerfection(pokemon) < keepIv)
                {
                    Logger.Write($"Did not Evolve {pokemon.PokemonId} ({pokemon.Cp} cp, {CalculatePokemonPerfection(pokemon).ToString("0.00")}%) (Under Requirement) ", LogLevel.Info);
                    continue;
                }

                var evolvePokemonOutProto = await _client.Inventory.EvolvePokemon((ulong)pokemon.Id);
                _stats.increasePokemonsTransfered();
                _stats.updateConsoleTitle(_inventory);
                if (evolvePokemonOutProto.Result == EvolvePokemonResponse.Types.Result.Success)
                    Logger.Write($"Evolved {pokemon.PokemonId} successfully for {evolvePokemonOutProto.ExperienceAwarded}xp", LogLevel.Info, ConsoleColor.DarkGreen);
                else
                    Logger.Write($"Failed to evolve {pokemon.PokemonId}. EvolvePokemonOutProto.Result was {evolvePokemonOutProto.Result}, stopping evolving {pokemon.PokemonId}", LogLevel.Info);

                await T.Delay(rand.Next(3000, 5000));
            }
        }


        public async Task EvolvePokemonFromList()
        {
            var pokemonToEvolve = await _inventory.GetPokemonToEvolve();
            Logger.Write($"Sorting through {pokemonToEvolve.ToList().Count} pokemon to evolve...", LogLevel.Info, ConsoleColor.DarkGreen);
            foreach (var pokemon in pokemonToEvolve)
            {
                // skip ones we do not want to evolve
                if (!EvolveList.Contains(pokemon.PokemonId))
                    continue;

                if (pokemon.Cp < UserSettings.EvolveOverCP && CalculatePokemonPerfection(pokemon) < UserSettings.EvolveOverIV)
                {
                    Logger.Write($"Did not Evolve {pokemon.PokemonId} ({pokemon.Cp} cp, {CalculatePokemonPerfection(pokemon).ToString("0.00")}%) (Under Requirement) ", LogLevel.Info);

                    continue;
                }

                var evolvePokemonOutProto = await _client.Inventory.EvolvePokemon((ulong)pokemon.Id);
                _stats.increasePokemonsTransfered();
                _stats.updateConsoleTitle(_inventory);
                if (evolvePokemonOutProto.Result == EvolvePokemonResponse.Types.Result.Success)
                    Logger.Write($"Evolved {pokemon.PokemonId} successfully for {evolvePokemonOutProto.ExperienceAwarded}xp", LogLevel.Info, ConsoleColor.DarkGreen);
                else
                    Logger.Write($"Failed to evolve {pokemon.PokemonId}. EvolvePokemonOutProto.Result was {evolvePokemonOutProto.Result}, stopping evolving {pokemon.PokemonId}", LogLevel.Info);


                await T.Delay(rand.Next(3500, 5000));
            }
        }

        public async Task RecycleItems()
        {
            var items = await _inventory.GetItemsToRecycle(_clientSettings);

            foreach (var item in items)
            {
                var transfer = await _client.Inventory.RecycleItem(item.ItemId, item.Count);
                Logger.Write($"Recycled {item.Count}x {item.ItemId}", LogLevel.Info, ConsoleColor.DarkYellow);
                await T.Delay(rand.Next(4000, 8000));
            }
        }

        public async Task<UseItemXpBoostResponse.Types.Result> UseLuckyEgg()
        {
            var resp = await _client.Inventory.UseItemXpBoost();

            return resp.Result;
        }

    }
}

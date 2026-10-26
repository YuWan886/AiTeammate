using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Small shims for the game API differences between 0.107.1 (stable) and 0.111.0 (beta).
/// Everything that needs a per-version branch lives here so the rest of the mod is
/// version-agnostic.
/// </summary>
internal static class GameCompat
{
    /// <summary>0.111 made StartRunLobby.MaxPlayers a private field.</summary>
    public static int MaxPlayers(StartRunLobby lobby)
    {
#if STS2_API_0_111_OR_LATER
        return AccessTools.Field(typeof(StartRunLobby), "_maxPlayers")?.GetValue(lobby) is int max ? max : 4;
#else
        return lobby.MaxPlayers;
#endif
    }

    /// <summary>0.111 replaced RunLobby.ConnectedPlayerIds with the Players/PlayerIds pair.</summary>
    public static IEnumerable<ulong> ConnectedPlayerIds(RunLobby lobby)
    {
#if STS2_API_0_111_OR_LATER
        return lobby.PlayerIds;
#else
        return lobby.ConnectedPlayerIds;
#endif
    }

    /// <summary>0.111 added the chooser parameter to SignalPlayerChoiceBegun.</summary>
    public static Task SignalPlayerChoiceBegun(PlayerChoiceContext context, Player chooser, PlayerChoiceOptions options)
    {
#if STS2_API_0_111_OR_LATER
        return context.SignalPlayerChoiceBegun(chooser, options);
#else
        return context.SignalPlayerChoiceBegun(options);
#endif
    }

    /// <summary>0.111 made the act index part of the vote action so stale votes can be ignored.</summary>
    public static GameAction CreateVoteToMoveToNextAct(Player player)
    {
#if STS2_API_0_111_OR_LATER
        return new VoteToMoveToNextActAction(player, player.RunState?.CurrentActIndex ?? 0);
#else
        return new VoteToMoveToNextActAction(player);
#endif
    }

    /// <summary>
    /// Marks the AI player as connected/ready in a resumed-run lobby. In 0.107 the lobby tracks
    /// connection and readiness in two sets; 0.111 folds both into the player list entry.
    /// </summary>
    public static void MarkLoadLobbyPlayerConnected(LoadRunLobby lobby, ulong netId)
    {
#if STS2_API_0_111_OR_LATER
        // LoadRunLobbyPlayer is a struct, so add a new entry or update the existing one in place.
        var index = lobby.Players.FindIndex(p => p.id == netId);
        MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer player;
        if (index < 0)
        {
            player = new MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer
            {
                id = netId,
                isModded = true,
                isReady = true
            };
            lobby.Players.Add(player);
        }
        else
        {
            player = lobby.Players[index];
            player.isReady = true;
            lobby.Players[index] = player;
        }

        // The game always hands the listener the same entry that lives in Players.
        lobby.LobbyListener.PlayerConnected(player);
#else
        if (lobby.ConnectedPlayerIds.Add(netId))
            lobby.LobbyListener.PlayerConnected(netId);

        var readyPlayers = (HashSet<ulong>?)AccessTools.Field(typeof(LoadRunLobby), "_readyPlayers")?.GetValue(lobby);
        if (readyPlayers != null && readyPlayers.Add(netId))
            lobby.LobbyListener.PlayerReadyChanged(netId);
#endif
    }
}

// 0.111 split the old LobbyPlayer struct into StartRunLobbyPlayer (start-run lobby) and
// LoadRunLobbyPlayer (resume lobby). The start-run shape is a superset of the 0.107.1 struct
// (it only added isModded), so an alias keeps the lobby code source-identical across variants.
#if STS2_API_0_111_OR_LATER
global using LobbyPlayer = MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer;
#else
global using LobbyPlayer = MegaCrit.Sts2.Core.Entities.Multiplayer.LobbyPlayer;
#endif

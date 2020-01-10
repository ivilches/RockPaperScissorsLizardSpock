﻿using GameApi.Proto;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RPSLS.Game.Api.Data;
using RPSLS.Game.Api.Data.Models;
using RPSLS.Game.Api.Services;
using RPSLS.Game.Multiplayer.Config;
using RPSLS.Game.Multiplayer.Services;
using System;
using System.Threading.Tasks;

namespace RPSLS.Game.Api.GrpcServices
{
    public class MultiplayerGameManagerService : MultiplayerGameManager.MultiplayerGameManagerBase
    {
        private const int FREE_TIER_MAX_REQUESTS = 10;
        private readonly GameStatusResponse _cancelledMatch = new GameStatusResponse { IsCancelled = true };

        private readonly IPlayFabService _playFabService;
        private readonly ITokenService _tokenService;
        private readonly IGameService _gameService;
        private readonly IMatchesRepository _repository;
        private readonly MultiplayerSettings _multiplayerSettings;
        private readonly ILogger<MultiplayerGameManagerService> _logger;

        public MultiplayerGameManagerService(
            IPlayFabService playFabService,
            ITokenService tokenService,
            IGameService gameService,
            IOptions<MultiplayerSettings> options,
            IMatchesRepository repository,
            ILogger<MultiplayerGameManagerService> logger)
        {
            _playFabService = playFabService;
            _tokenService = tokenService;
            _gameService = gameService;
            _repository = repository;
            _multiplayerSettings = options.Value;
            _logger = logger;

            if (_multiplayerSettings.Token.TicketStatusWait < 60000 / FREE_TIER_MAX_REQUESTS)
            {
                _logger.LogWarning($"PlayFab free tier limits the Get Matchmaking Ticket requests to a max of {FREE_TIER_MAX_REQUESTS} per minute. " +
                    "A MatchmakingRateLimitExceeded error might occur while waiting for a multiplayer match");
            }
        }

        public override async Task<CreatePairingResponse> CreatePairing(CreatePairingRequest request, ServerCallContext context)
        {
            var token = await _tokenService.CreateToken(request.Username);
            _logger.LogInformation($"New token created for user {request.Username}: {token}");
            return new CreatePairingResponse() { Token = token };
        }

        public override async Task<Empty> JoinPairing(JoinPairingRequest request, ServerCallContext context)
        {
            await _tokenService.JoinToken(request.Username, request.Token);
            return new Empty();
        }

        public override async Task PairingStatus(PairingStatusRequest request, IServerStreamWriter<PairingStatusResponse> responseStream, ServerCallContext context)
        {
            var username = request.Username;
            var matchResult = await _tokenService.GetMatch(username);
            while (string.IsNullOrWhiteSpace(matchResult.TicketId))
            {
                // If ticket is null it might be due a limit exceeded error, retry before moving to next step
                await responseStream.WriteAsync(CreateMatchStatusResponse("RateLimitExceeded"));
                await Task.Delay(_multiplayerSettings.Token.TicketListWait);
                matchResult = await _tokenService.GetMatch(username);
            }

            while (!matchResult.Finished && !context.CancellationToken.IsCancellationRequested)
            {
                await responseStream.WriteAsync(CreateMatchStatusResponse(matchResult.Status));
                await Task.Delay(_multiplayerSettings.Token.TicketStatusWait);
                matchResult = await _tokenService.GetMatch(username, matchResult.TicketId);
            }

            await responseStream.WriteAsync(CreateMatchStatusResponse(matchResult.Status, matchResult.MatchId));
            if (request.IsMaster)
            {
                await _repository.CreateMatch(matchResult.MatchId, username, matchResult.Opponent);
            }
        }

        public override async Task GameStatus(GameStatusRequest request, IServerStreamWriter<GameStatusResponse> responseStream, ServerCallContext context)
        {
            const string UnknownUser = "-";
            var dto = await _repository.GetMatch(request.MatchId);
            while (dto.PlayerName == UnknownUser && dto.Challenger.Name == UnknownUser)
            {
                await Task.Delay(_multiplayerSettings.GameStatusUpdateDelay);
                dto = await _repository.GetMatch(request.MatchId);
            }

            var isMaster = dto.PlayerName == request.Username;
            var gameStatus = isMaster ? CreateGameStatusForMaster(dto) : CreateGameStatusForOpponent(dto);
            await responseStream.WriteAsync(gameStatus);
            _logger.LogDebug($"{request.Username} -> Updated {gameStatus.User} vs {gameStatus.Challenger} /{gameStatus.UserPick}-{gameStatus.ChallengerPick}/");
            while (!context.CancellationToken.IsCancellationRequested && gameStatus.Result == Result.Pending)
            {
                await Task.Delay(_multiplayerSettings.GameStatusUpdateDelay);
                dto = await _repository.GetMatch(request.MatchId);

                if (dto == null)
                {
                    _logger.LogDebug($"{request.Username} -> dto is null");
                    await responseStream.WriteAsync(_cancelledMatch);
                    return;
                }

                var matchExpired = DateTime.UtcNow.AddSeconds(-_multiplayerSettings.GameStatusMaxWait) > dto.WhenUtc;
                if (isMaster && matchExpired)
                {
                    _logger.LogDebug($"{request.Username} -> match expired");
                    await _repository.DeleteMatch(request.MatchId);
                    await responseStream.WriteAsync(_cancelledMatch);
                    return;
                }

                gameStatus = isMaster ? CreateGameStatusForMaster(dto) : CreateGameStatusForOpponent(dto);
                _logger.LogDebug($"{request.Username} -> Updated {gameStatus.User} vs {gameStatus.Challenger} /{gameStatus.UserPick}-{gameStatus.ChallengerPick}/");
                await responseStream.WriteAsync(gameStatus);
            }
        }

        public override async Task<Empty> Pick(PickRequest request, ServerCallContext context)
        {
            var dto = await _repository.SaveMatchPick(request.MatchId, request.Username, request.Pick);
            if (!string.IsNullOrWhiteSpace(dto.ChallengerMove?.Text) && !string.IsNullOrWhiteSpace(dto.PlayerMove?.Text))
            {
                var result = _gameService.Check(dto.PlayerMove.Value, dto.ChallengerMove.Value);
                await _playFabService.UpdateStats(dto.PlayerName, dto.Result.Value == (int)Result.Player);
                await _playFabService.UpdateStats(dto.Challenger.Name, dto.Result.Value == (int)Result.Challenger);
                await _repository.SaveMatchResult(request.MatchId, result);
            }

            return new Empty();
        }

        private static PairingStatusResponse CreateMatchStatusResponse(string status, string matchId = null)
            => new PairingStatusResponse()
            {
                Status = status ?? string.Empty,
                MatchId = matchId ?? string.Empty,
            };

        private static GameStatusResponse CreateGameStatusForMaster(MatchDto match)
        {
            return new GameStatusResponse
            {
                User = match.PlayerName,
                UserPick = match.PlayerMove.Value,
                Challenger = match.Challenger?.Name ?? "-",
                ChallengerPick = match.ChallengerMove.Value,
                Result = (Result)match.Result.Value,
                IsMaster = true,
                IsCancelled = false,
                IsFinished = match.Result.Value != (int)Result.Pending
            };
        }

        private static GameStatusResponse CreateGameStatusForOpponent(MatchDto match)
        {
            var result = match.Result.Value switch
            {
                1 => Result.Challenger,
                2 => Result.Player,
                _ => (Result)match.Result.Value
            };

            return new GameStatusResponse
            {
                User = match.Challenger.Name,
                UserPick = match.ChallengerMove.Value,
                Challenger = match.PlayerName ?? "-",
                ChallengerPick = match.PlayerMove.Value,
                Result = result,
                IsMaster = false,
                IsCancelled = false,
                IsFinished = result != Result.Pending
            };
        }
    }
}

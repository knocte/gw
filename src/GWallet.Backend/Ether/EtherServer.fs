﻿namespace GWallet.Backend.Ether

open System
open System.IO
open System.Net
open System.Numerics
open System.Linq
open System.Threading
open System.Threading.Tasks

open Nethereum.Util
open Nethereum.Hex.HexTypes
open Nethereum.Web3
open Nethereum.RPC.Eth.DTOs
open Nethereum.StandardTokenEIP20.ContractDefinition

open GWallet.Backend

type BalanceType =
    | Unconfirmed
    | Confirmed

type SomeWeb3(url: string) =
    inherit Web3(url)

    member val Url = url with get

type TransactionStatusDetails =
    {
        GasUsed: BigInteger
        Status: BigInteger
    }

module Web3ServerSeedList =

    // -------------- SERVERS TO REVIEW ADDING TO THE REGISTRY: -----------------------------
    //let private PUBLIC_WEB3_API_ETH_INFURA = "https://mainnet.infura.io:8545" ?
    // not sure why the below one doesn't work, gives some JSON error
    //let private ethWeb3EtherScan = SomeWeb3 "https://api.etherscan.io/api"

    // TODO: add the one from https://etcchain.com/api/ too
    // FIXME: the below one doesn't seem to work; we should include it anyway and make the algorithm discard it at runtime
    //let private etcWeb3CommonWealthMantis = SomeWeb3("https://etc-mantis.callisto.network")
    // --------------------------------------------------------------------------------------

    let private GetEtherServers (currency: Currency): List<ServerDetails> =
        let baseCurrency =
            if currency = Currency.ETC || currency = Currency.ETH then
                currency
            elif currency.IsEthToken() then
                Currency.ETH
            else
                failwithf "Assertion failed: Ether currency %A not supported?" currency
        Caching.Instance.GetServers baseCurrency |> List.ofSeq

    let Randomize currency =
        let serverList = GetEtherServers currency
        Shuffler.Unsort serverList


module Server =

    let private Web3Server (serverDetails: ServerDetails) =
        match serverDetails.ServerInfo.ConnectionType with
        | { Protocol = Tcp _ ; Encrypted = _ } ->
            failwithf "Ether server of TCP connection type?: %s" serverDetails.ServerInfo.NetworkPath
        | { Protocol = Http ; Encrypted = encrypted } ->
            let protocol =
                if encrypted then
                    "https"
                else
                    "http"
            let uri = sprintf "%s://%s" protocol serverDetails.ServerInfo.NetworkPath
            SomeWeb3 uri

    let HttpRequestExceptionMatchesErrorCode (ex: Http.HttpRequestException) (errorCode: int): bool =
        ex.Message.StartsWith(sprintf "%d " errorCode) || ex.Message.Contains(sprintf " %d " errorCode)

    let exMsg = "Could not communicate with EtherServer"
    let PerformEtherRemoteCallWithTimeout<'T,'R> (job: Async<'R>): Async<'R> = async {
        let! maybeResult = FSharpUtil.WithTimeout Config.DEFAULT_NETWORK_TIMEOUT job
        match maybeResult with
        | None ->
            return raise <| ServerTimedOutException("Timeout when trying to communicate with Ether server")
        | Some result ->
            return result
    }

    // we can possibly/hopefully remove this shitty method when this bug is fixed: https://github.com/dotnet/corefx/issues/20296
    // TODO: remove this function below once we finishing tracking down (fixing)
    //       https://gitlab.com/knocte/geewallet/issues/125
    let MaybeRethrowShittyTaskCanceledExceptionComingFromHttpClient (ex: Exception): unit =
        let maybeTaskCanceledEx = FSharpUtil.FindException<TaskCanceledException> ex
        match maybeTaskCanceledEx with
        | Some taskCanceledEx ->
            raise <| ServerTimedOutException("Possibly the operation timed out...", taskCanceledEx)
        | None ->
            ()

    let MaybeRethrowWebException (ex: Exception): unit =
        let maybeWebEx = FSharpUtil.FindException<WebException> ex
        match maybeWebEx with
        | Some webEx ->

            // TODO: send a warning in Sentry
            if webEx.Status = WebExceptionStatus.UnknownError then
                raise <| ServerUnreachableException(exMsg, webEx)

            if webEx.Status = WebExceptionStatus.NameResolutionFailure then
                raise <| ServerCannotBeResolvedException(exMsg, webEx)
            if webEx.Status = WebExceptionStatus.ReceiveFailure then
                raise <| ServerTimedOutException(exMsg, webEx)
            if webEx.Status = WebExceptionStatus.ConnectFailure then
                raise <| ServerUnreachableException(exMsg, webEx)

            if webEx.Status = WebExceptionStatus.SecureChannelFailure then
                raise <| ServerChannelNegotiationException(exMsg, webEx.Status, webEx)
            if webEx.Status = WebExceptionStatus.RequestCanceled then
                raise <| ServerChannelNegotiationException(exMsg, webEx.Status, webEx)
            if webEx.Status = WebExceptionStatus.TrustFailure then
                raise <| ServerChannelNegotiationException(exMsg, webEx.Status, webEx)

            // as Ubuntu 18.04's Mono (4.6.2) doesn't have TLS1.2 support, this below is more likely to happen:
            if not Networking.Tls12Support then
                if webEx.Status = WebExceptionStatus.SendFailure then
                    raise <| ServerUnreachableException(exMsg, webEx)

            raise <| UnhandledWebException(webEx.Status, webEx)

        | None ->
            ()

    let MaybeRethrowHttpRequestException (ex: Exception): unit =
        let maybeHttpReqEx = FSharpUtil.FindException<Http.HttpRequestException> ex
        match maybeHttpReqEx with
        | Some httpReqEx ->
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int CloudFlareError.ConnectionTimeOut) then
                raise <| ServerTimedOutException(exMsg, httpReqEx)
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int CloudFlareError.OriginUnreachable) then
                raise <| ServerTimedOutException(exMsg, httpReqEx)

            if HttpRequestExceptionMatchesErrorCode httpReqEx (int CloudFlareError.OriginSslHandshakeError) then
                raise <| ServerChannelNegotiationException(exMsg, CloudFlareError.OriginSslHandshakeError, httpReqEx)

            if HttpRequestExceptionMatchesErrorCode httpReqEx (int CloudFlareError.WebServerDown) then
                raise <| ServerUnreachableException(exMsg, CloudFlareError.WebServerDown, httpReqEx)
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int HttpStatusCode.BadGateway) then
                raise <| ServerUnreachableException(exMsg, HttpStatusCode.BadGateway, httpReqEx)
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int HttpStatusCode.GatewayTimeout) then
                raise <| ServerUnreachableException(exMsg, HttpStatusCode.GatewayTimeout, httpReqEx)

            if HttpRequestExceptionMatchesErrorCode httpReqEx (int HttpStatusCode.ServiceUnavailable) then
                raise <| ServerUnavailableException(exMsg, httpReqEx)

            // TODO: maybe in these cases below, blacklist the server somehow if it keeps giving this error:
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int HttpStatusCode.Forbidden) then
                raise <| ServerMisconfiguredException(exMsg, httpReqEx)
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int HttpStatusCode.Unauthorized) then
                raise <| ServerMisconfiguredException(exMsg, httpReqEx)
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int HttpStatusCode.MethodNotAllowed) then
                raise <| ServerMisconfiguredException(exMsg, httpReqEx)
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int HttpStatusCode.InternalServerError) then
                raise <| ServerUnavailableException(exMsg, httpReqEx)

            if HttpRequestExceptionMatchesErrorCode
                httpReqEx (int HttpStatusCodeNotPresentInTheBcl.TooManyRequests) then
                    raise <| ServerRestrictiveException(exMsg, httpReqEx)
            if HttpRequestExceptionMatchesErrorCode httpReqEx (int HttpStatusCodeNotPresentInTheBcl.FrozenSite) then
                raise <| ServerUnavailableException(exMsg, httpReqEx)

            // weird "IOException: The server returned an invalid or unrecognized response." since Mono 6.4.x (vs16.3)
            if (FSharpUtil.FindException<IOException> httpReqEx).IsSome then
                raise <| ServerMisconfiguredException(exMsg, httpReqEx)
        | _ ->
            ()

    let MaybeRethrowRpcResponseException (ex: Exception): unit =
        let maybeRpcResponseEx = FSharpUtil.FindException<JsonRpcSharp.Client.RpcResponseException> ex
        match maybeRpcResponseEx with
        | Some rpcResponseEx ->
            if rpcResponseEx.RpcError <> null then
                if (rpcResponseEx.RpcError.Code = int RpcErrorCode.StatePruningNodeOrMissingTrieNode) then
                    if (not (rpcResponseEx.RpcError.Message.Contains "pruning=archive")) &&
                       (not (rpcResponseEx.RpcError.Message.Contains "missing trie node")) then
                        raise <| Exception(
                                     sprintf "Expecting 'pruning=archive' or 'missing trie node' in message of a %d code"
                                                   (int RpcErrorCode.StatePruningNodeOrMissingTrieNode), rpcResponseEx)
                    else
                        raise <| ServerMisconfiguredException(exMsg, rpcResponseEx)
                if (rpcResponseEx.RpcError.Code = int RpcErrorCode.UnknownBlockNumber) then
                    raise <| ServerMisconfiguredException(exMsg, rpcResponseEx)
                if rpcResponseEx.RpcError.Code = int RpcErrorCode.GatewayTimeout then
                    raise <| ServerMisconfiguredException(exMsg, rpcResponseEx)
                raise <| Exception(sprintf "RpcResponseException with RpcError Code %d and Message %s (%s)"
                                         rpcResponseEx.RpcError.Code
                                         rpcResponseEx.RpcError.Message
                                         rpcResponseEx.Message,
                                   rpcResponseEx)
        | None ->
            ()

    let MaybeRethrowRpcClientTimeoutException (ex: Exception): unit =
        let maybeRpcTimeoutException =
            FSharpUtil.FindException<JsonRpcSharp.Client.RpcClientTimeoutException> ex
        match maybeRpcTimeoutException with
        | Some rpcTimeoutEx ->
            raise <| ServerTimedOutException(exMsg, rpcTimeoutEx)
        | None ->
            ()

    let MaybeRethrowNetworkingException (ex: Exception): unit =
        let maybeSocketRewrappedException = Networking.FindExceptionToRethrow ex exMsg
        match maybeSocketRewrappedException with
        | Some socketRewrappedException ->
            raise socketRewrappedException
        | None ->
            ()

    // this could be a Xamarin.Android bug (see https://gitlab.com/knocte/geewallet/issues/119)
    let MaybeRethrowObjectDisposedException (ex: Exception): unit =
        let maybeRpcUnknownEx = FSharpUtil.FindException<JsonRpcSharp.Client.RpcClientUnknownException> ex
        match maybeRpcUnknownEx with
        | Some rpcUnknownEx ->
            let maybeObjectDisposedEx = FSharpUtil.FindException<ObjectDisposedException> ex
            match maybeObjectDisposedEx with
            | Some objectDisposedEx ->
                if objectDisposedEx.Message.Contains "MobileAuthenticatedStream" then
                    raise <| ProtocolGlitchException(objectDisposedEx.Message, objectDisposedEx)
            | None ->
                ()
        | None ->
            ()

    // this could be a mono 6.0.x bug (see https://gitlab.com/knocte/geewallet/issues/121)
    let MaybeRethrowSslException (ex: Exception): unit =
        let maybeRpcUnknownEx = FSharpUtil.FindException<JsonRpcSharp.Client.RpcClientUnknownException> ex
        match maybeRpcUnknownEx with
        | Some rpcUnknownEx ->
            let maybeHttpReqEx = FSharpUtil.FindException<Http.HttpRequestException> ex
            match maybeHttpReqEx with
            | Some httpReqEx ->
                if httpReqEx.Message.Contains "SSL" then
                    let maybeIOEx = FSharpUtil.FindException<IOException> ex
                    match maybeIOEx with
                    | Some ioEx ->
                        raise <| ProtocolGlitchException(ioEx.Message, ex)
                    | None ->
                        let maybeSecEx =
                            FSharpUtil.FindException<System.Security.Authentication.AuthenticationException> ex
                        match maybeSecEx with
                        | Some secEx ->
                            raise <| ProtocolGlitchException(secEx.Message, ex)
                        | None ->
                            ()
            | None ->
                ()
        | None ->
            ()

    let private ReworkException (ex: Exception): unit =

        MaybeRethrowShittyTaskCanceledExceptionComingFromHttpClient ex

        MaybeRethrowWebException ex

        MaybeRethrowHttpRequestException ex

        MaybeRethrowRpcResponseException ex

        MaybeRethrowRpcClientTimeoutException ex

        MaybeRethrowNetworkingException ex

        MaybeRethrowObjectDisposedException ex

        MaybeRethrowSslException ex


    let private NumberOfParallelJobsForMode mode =
        match mode with
        | ServerSelectionMode.Fast -> 3u
        | ServerSelectionMode.Analysis -> 2u

    let private FaultTolerantParallelClientInnerSettings (numberOfConsistentResponsesRequired: uint32)
                                                         (mode: ServerSelectionMode)
                                                         maybeConsistencyConfig =

        let consistencyConfig =
            match maybeConsistencyConfig with
            | None -> SpecificNumberOfConsistentResponsesRequired numberOfConsistentResponsesRequired
            | Some specificConsistencyConfig -> specificConsistencyConfig

        {
            NumberOfParallelJobsAllowed = NumberOfParallelJobsForMode mode
            NumberOfRetries = Config.NUMBER_OF_RETRIES_TO_SAME_SERVERS;
            NumberOfRetriesForInconsistency = Config.NUMBER_OF_RETRIES_TO_SAME_SERVERS;
            ExceptionHandler = Some
                (
                    fun ex ->
                        let exToReport =
                            if (FSharpUtil.FindException<TaskCanceledException> ex).IsSome then
                                // TODO: remove this below once we finishing tracking down (fixing)
                                //       https://gitlab.com/knocte/geewallet/issues/125
                                UnexpectedTaskCanceledException("Cancellation of subjob", ex) :> Exception
                            else
                                ex

                        Infrastructure.ReportWarning exToReport
                )
            ExtraProtectionAgainstUnfoundedCancellations = true
            ResultSelectionMode =
                Selective
                    {
                        ServerSelectionMode = mode
                        ConsistencyConfig = consistencyConfig
                        ReportUncanceledJobs = true
                    }
        }

    let private FaultTolerantParallelClientDefaultSettings (currency: Currency) (mode: ServerSelectionMode) =
        let numberOfConsistentResponsesRequired =
            if not Networking.Tls12Support then
                1u
            else
                2u
        FaultTolerantParallelClientInnerSettings numberOfConsistentResponsesRequired
                                                 mode

    let private FaultTolerantParallelClientSettingsForBalanceCheck (currency: Currency)
                                                                   (mode: ServerSelectionMode)
                                                                   (cacheOrInitialBalanceMatchFunc: decimal->bool) =
        let consistencyConfig =
            if mode = ServerSelectionMode.Fast then
                Some (OneServerConsistentWithCertainValueOrTwoServers cacheOrInitialBalanceMatchFunc)
            else
                None
        FaultTolerantParallelClientDefaultSettings currency mode consistencyConfig

    let private FaultTolerantParallelClientSettingsForBroadcast () =
        FaultTolerantParallelClientInnerSettings 1u ServerSelectionMode.Fast None

    let private faultTolerantEtherClient =
        JsonRpcSharp.Client.RpcClient.ConnectionTimeout <- Config.DEFAULT_NETWORK_TIMEOUT
        FaultTolerantParallelClient<ServerDetails,ServerDiscardedException> Caching.Instance.SaveServerLastStat


    let Web3ServerToRetrievalFunc (server: ServerDetails)
                                  (web3ClientFunc: SomeWeb3->Async<'R>)
                                      : Async<'R> =

        let HandlePossibleEtherFailures (job: Async<'R>): Async<'R> =
            async {
                try
                    let! result = PerformEtherRemoteCallWithTimeout job
                    return result
                with
                | ex ->
                    ReworkException ex

                    return raise <| FSharpUtil.ReRaise ex
            }
        async {
            let web3Server = Web3Server server
            try
                return! HandlePossibleEtherFailures (web3ClientFunc web3Server)

            // NOTE: try to make this 'with' block be in sync with the one in UtxoCoinAccount:GetRandomizedFuncs()
            with
            | :? CommunicationUnsuccessfulException as ex ->
                let msg = sprintf "%s: %s" (ex.GetType().FullName) ex.Message
                return raise <| ServerDiscardedException(msg, ex)
            | ex ->
                return raise <| Exception(sprintf "Some problem when connecting to '%s'"
                                                  server.ServerInfo.NetworkPath, ex)
        }

    // FIXME: seems there's some code duplication between this function and UtxoCoinAccount.fs's GetServerFuncs function
    //        and room for simplification to not pass a new ad-hoc delegate?
    let GetServerFuncs<'R> (web3Func: SomeWeb3->Async<'R>)
                           (etherServers: seq<ServerDetails>)
                               : seq<Server<ServerDetails,'R>> =
        let Web3ServerToGenericServer (web3ClientFunc: SomeWeb3->Async<'R>)
                                      (etherServer: ServerDetails)
                                              : Server<ServerDetails,'R> =
            {
                Details = etherServer
                Retrieval = Web3ServerToRetrievalFunc etherServer web3ClientFunc
            }

        let serverFuncs =
            Seq.map (Web3ServerToGenericServer web3Func)
                    etherServers
        serverFuncs

    let private GetRandomizedFuncs<'R> (currency: Currency)
                                       (web3Func: SomeWeb3->Async<'R>)
                                           : List<Server<ServerDetails,'R>> =
        let etherServers = Web3ServerSeedList.Randomize currency
        GetServerFuncs web3Func etherServers
            |> List.ofSeq

    let GetTransactionCount (currency: Currency) (address: string)
                                : Async<HexBigInteger> =
        async {
            let web3Funcs =
                let web3Func (web3: Web3): Async<HexBigInteger> =
                        async {
                            let! cancelToken = Async.CancellationToken
                            let task =
                                web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(address, null, cancelToken)
                            return! Async.AwaitTask task
                        }
                GetRandomizedFuncs currency web3Func
            return! faultTolerantEtherClient.Query
                (FaultTolerantParallelClientDefaultSettings currency ServerSelectionMode.Fast None)
                web3Funcs
        }

    let private NUMBER_OF_CONFIRMATIONS_TO_CONSIDER_BALANCE_CONFIRMED = BigInteger(45)
    let private GetBlockToCheckForConfirmedBalance(web3: Web3): Async<BlockParameter> =
        async {
            let! cancelToken = Async.CancellationToken
            let! latestBlock =
                web3.Eth.Blocks.GetBlockNumber.SendRequestAsync (null, cancelToken)
                    |> Async.AwaitTask
            if (latestBlock = null) then
                failwith "latestBlock somehow is null"

            let blockToCheck = BigInteger.Subtract(latestBlock.Value,
                                                   NUMBER_OF_CONFIRMATIONS_TO_CONSIDER_BALANCE_CONFIRMED)

            if blockToCheck.Sign < 0 then
                let errMsg = sprintf
                                 "Looks like we received a wrong latestBlock(%s) because the substract was negative(%s)"
                                     (latestBlock.Value.ToString())
                                     (blockToCheck.ToString())
                raise <| ServerMisconfiguredException errMsg

            return BlockParameter(HexBigInteger(blockToCheck))
        }

    let private GetConfirmedEtherBalanceInternal (web3: Web3) (publicAddress: string): Async<HexBigInteger> =
        async {
            let! blockForConfirmationReference = GetBlockToCheckForConfirmedBalance web3
(*
            if (Config.DebugLog) then
                Console.Error.WriteLine (sprintf "Last block number and last confirmed block number: %s: %s"
                                                 (latestBlock.Value.ToString()) (blockForConfirmationReference.BlockNumber.Value.ToString()))
*)

            let! cancelToken = Async.CancellationToken
            cancelToken.ThrowIfCancellationRequested()
            let! balance =
                web3.Eth.GetBalance.SendRequestAsync (publicAddress,
                                                      blockForConfirmationReference,
                                                      null,
                                                      cancelToken)
                    |> Async.AwaitTask
            return balance
        }

    let private BalanceMatchWithCacheOrInitialBalance address currency someRetrievedBalance =
        if Caching.Instance.FirstRun then
            someRetrievedBalance = 0m
        else
            match Caching.Instance.TryRetrieveLastCompoundBalance address currency with
            | None -> false
            | Some balance -> someRetrievedBalance = balance

    let GetEtherBalance (currency: Currency)
                        (address: string)
                        (balType: BalanceType)
                        (mode: ServerSelectionMode)
                        (cancelSourceOption: Option<CancellationTokenSource>)
                                     : Async<decimal> =
        async {
            let web3Funcs =
                let web3Func (web3: Web3): Async<decimal> = async {
                    let! balance =
                        match balType with
                        | BalanceType.Confirmed ->
                            GetConfirmedEtherBalanceInternal web3 address
                        | BalanceType.Unconfirmed ->
                            async {
                                let! cancelToken = Async.CancellationToken
                                let task = web3.Eth.GetBalance.SendRequestAsync (address, null, cancelToken)
                                return! Async.AwaitTask task
                            }
                    if Object.ReferenceEquals(balance, null) then
                        failwith "Weird null response from balance job"
                    return UnitConversion.Convert.FromWei(balance.Value, UnitConversion.EthUnit.Ether)
                }
                GetRandomizedFuncs currency web3Func

            let query =
                match cancelSourceOption with
                | None ->
                    faultTolerantEtherClient.Query
                | Some cancelSource ->
                    faultTolerantEtherClient.QueryWithCancellation cancelSource

            return! query
                        (FaultTolerantParallelClientSettingsForBalanceCheck
                            currency mode (BalanceMatchWithCacheOrInitialBalance address currency))
                        web3Funcs
        }

    let private GetConfirmedTokenBalanceInternal (web3: Web3) (publicAddress: string): Async<decimal> =
        if (web3 = null) then
            invalidArg "web3" "web3 argument should not be null"

        async {
            let! blockForConfirmationReference = GetBlockToCheckForConfirmedBalance web3
            let balanceOfFunctionMsg = BalanceOfFunction(Owner = publicAddress)

            let contractHandler = web3.Eth.GetContractHandler(TokenManager.DAI_CONTRACT_ADDRESS)
            if (contractHandler = null) then
                failwith "contractHandler somehow is null"

            let! cancelToken = Async.CancellationToken
            cancelToken.ThrowIfCancellationRequested()
            let! balance = contractHandler.QueryAsync<BalanceOfFunction,BigInteger>
                                    (balanceOfFunctionMsg,
                                     blockForConfirmationReference,
                                     cancelToken) |> Async.AwaitTask
            return UnitConversion.Convert.FromWei(balance, UnitConversion.EthUnit.Ether)
        }


    let GetTokenBalance (currency: Currency)
                        (address: string)
                        (balType: BalanceType)
                        (mode: ServerSelectionMode)
                        (cancelSourceOption: Option<CancellationTokenSource>)
                            : Async<decimal> =
        async {
            let web3Funcs =
                let web3Func (web3: Web3): Async<decimal> =
                        match balType with
                        | BalanceType.Confirmed ->
                            GetConfirmedTokenBalanceInternal web3 address
                        | BalanceType.Unconfirmed ->
                            let tokenService = TokenManager.DaiContract web3
                            async {
                                let! cancelToken = Async.CancellationToken
                                let task = tokenService.BalanceOfQueryAsync (address, null, cancelToken)
                                let! balance = Async.AwaitTask task
                                return UnitConversion.Convert.FromWei(balance, UnitConversion.EthUnit.Ether)
                            }
                GetRandomizedFuncs currency web3Func

            let query =
                match cancelSourceOption with
                | None ->
                    faultTolerantEtherClient.Query
                | Some cancelSource ->
                    faultTolerantEtherClient.QueryWithCancellation cancelSource

            return! query
                        (FaultTolerantParallelClientSettingsForBalanceCheck
                            currency mode (BalanceMatchWithCacheOrInitialBalance address currency))
                        web3Funcs
        }

    let EstimateTokenTransferFee (baseCurrency: Currency) (account: IAccount) (amount: decimal) destination
                                     : Async<HexBigInteger> =
        async {
            let web3Funcs =
                let web3Func (web3: Web3): Async<HexBigInteger> =
                    let contractHandler = web3.Eth.GetContractHandler(TokenManager.DAI_CONTRACT_ADDRESS)
                    let amountInWei = UnitConversion.Convert.ToWei(amount, UnitConversion.EthUnit.Ether)
                    let transferFunctionMsg = TransferFunction(FromAddress = account.PublicAddress,
                                                               To = destination,
                                                               Value = amountInWei)
                    async {
                            let! cancelToken = Async.CancellationToken
                            let task =
                                contractHandler.EstimateGasAsync<TransferFunction>(transferFunctionMsg, cancelToken)
                            return! Async.AwaitTask task
                    }
                GetRandomizedFuncs account.Currency web3Func
            return! faultTolerantEtherClient.Query
                        (FaultTolerantParallelClientDefaultSettings baseCurrency ServerSelectionMode.Fast None)
                        web3Funcs
        }

    let private AverageGasPrice (gasPricesFromDifferentServers: List<HexBigInteger>): HexBigInteger =
        let sum = gasPricesFromDifferentServers.Select(fun hbi -> hbi.Value)
                                               .Aggregate(fun bi1 bi2 -> BigInteger.Add(bi1, bi2))
        let avg = BigInteger.Divide(sum, BigInteger(gasPricesFromDifferentServers.Length))
        HexBigInteger(avg)

    let GetGasPrice (currency: Currency)
        : Async<HexBigInteger> =
        async {
            let web3Funcs =
                let web3Func (web3: Web3): Async<HexBigInteger> =
                        async {
                            let! cancelToken = Async.CancellationToken
                            let task = web3.Eth.GasPrice.SendRequestAsync(null, cancelToken)
                            return! Async.AwaitTask task
                        }
                GetRandomizedFuncs currency web3Func
            let minResponsesRequired = 2u
            return! faultTolerantEtherClient.Query
                        (FaultTolerantParallelClientDefaultSettings
                            currency ServerSelectionMode.Fast
                            (Some (AverageBetweenResponses (minResponsesRequired, AverageGasPrice))))
                        web3Funcs

        }

    let BroadcastTransaction (currency: Currency) (transaction: string)
        : Async<string> =
        let insufficientFundsMsg = "Insufficient funds"

        async {
            let web3Funcs =
                let web3Func (web3: Web3): Async<string> =
                        async {
                            let! cancelToken = Async.CancellationToken
                            let task =
                                web3.Eth.Transactions.SendRawTransaction.SendRequestAsync(transaction, null, cancelToken)
                            return! Async.AwaitTask task
                        }
                GetRandomizedFuncs currency web3Func
            try
                return! faultTolerantEtherClient.Query
                            (FaultTolerantParallelClientSettingsForBroadcast ())
                            web3Funcs
            with
            | ex ->
                match FSharpUtil.FindException<JsonRpcSharp.Client.RpcResponseException> ex with
                | None ->
                    return raise (FSharpUtil.ReRaise ex)
                | Some rpcResponseException ->
                    // FIXME: this is fragile, ideally should respond with an error code
                    if rpcResponseException.Message.StartsWith(insufficientFundsMsg,
                                                               StringComparison.InvariantCultureIgnoreCase) then
                        return raise InsufficientFunds
                    else
                        return raise (FSharpUtil.ReRaise ex)
        }

    let private GetTransactionDetailsFromTransactionReceipt (currency: Currency) (txHash: string)
                                          : Async<TransactionStatusDetails> =
        async {
            let web3Funcs =
                let web3Func (web3: Web3): Async<TransactionStatusDetails> =
                    async {
                        let! cancelToken = Async.CancellationToken
                        let task =
                            web3.TransactionManager.TransactionReceiptService.PollForReceiptAsync(txHash, cancelToken)
                        let! transactionReceipt = Async.AwaitTask task
                        return {
                            GasUsed = transactionReceipt.GasUsed.Value
                            Status = transactionReceipt.Status.Value
                        }
                    }
                GetRandomizedFuncs currency web3Func
            return! faultTolerantEtherClient.Query
                (FaultTolerantParallelClientDefaultSettings currency ServerSelectionMode.Fast None)
                web3Funcs
        }

    let IsOutOfGas (currency: Currency) (txHash: string) (spentGas: int64): Async<bool> =
        async {
            let! transactionStatusDetails = GetTransactionDetailsFromTransactionReceipt currency txHash
            let failureStatus = BigInteger.Zero
            return transactionStatusDetails.Status = failureStatus &&
                   transactionStatusDetails.GasUsed = BigInteger(spentGas)
        }

    let private GetContractCode (baseCurrency: Currency) (address: string)
                                    : Async<string> =
        async {
            let web3Funcs =
                let web3Func (web3: Web3): Async<string> =
                        async {
                            let! cancelToken = Async.CancellationToken
                            let task = web3.Eth.GetCode.SendRequestAsync(address, null, cancelToken)
                            return! Async.AwaitTask task
                        }
                GetRandomizedFuncs baseCurrency web3Func
            return! faultTolerantEtherClient.Query
                (FaultTolerantParallelClientDefaultSettings baseCurrency ServerSelectionMode.Fast None)
                web3Funcs
        }

    let CheckIfAddressIsAValidPaymentDestination (currency: Currency) (address: string): Async<unit> =
        async {
            let! contractCode = GetContractCode currency address
            let emptyContract = "0x"

            if not (contractCode.StartsWith emptyContract) then
                failwithf "GetCode API should always return a string starting with %s, but got: %s"
                          emptyContract contractCode
            elif contractCode <> emptyContract then
                return raise <| InvalidDestinationAddress "Sending to contract addresses is not supported yet. Supply a normal address please."
        }


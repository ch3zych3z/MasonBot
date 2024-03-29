namespace Handlers

open Funogram.Types
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Types
open Funogram.Telegram.Bot

open Logging
open MasonCore

module Basic =

    // Matchers
    let matchTextMessage expectedText ctx =
        let message = ctx.Update.Message
        match message with
        | Some {Chat = chat; Text = Some text } ->
            if text = expectedText then Some chat else None
        | _ -> None

    let matchAnyMessage ctx = // TODO: delete this
        let message = ctx.Update.Message
        match message with
        | Some {Chat = chat; Text = Some text} ->
            Some chat, Some text
        | Some {Chat = chat} ->
            Some chat, None
        | _ -> None, None

    let matchSimpleCallbackMessage expectedCallback ctx =
        let callback = ctx.Update.CallbackQuery
        match callback with
        | Some { Data = Some data; From = from} ->
            let content = Callback.string2SimpleCallbackContent data
            match content with
            | Some callbackData ->
                if callbackData = expectedCallback then Some from else None
            | _ -> None
        | _ -> None

    let matchCallbackMessage ctx =
        let callback = ctx.Update.CallbackQuery
        match callback with
        | Some { Data = Some data; From = from} ->
            let content = Callback.string2SimpleCallbackContent data
            match content with
            | Some callbackData ->
                Some (from, callbackData)
            | _ -> None
        | _ -> None

    let matchStateWithSimpleCallback expectedState expectedCallback ctx =
        let callback = ctx.Update.CallbackQuery
        match callback with
        | Some { Data = Some data; From = from} ->
            let content = Callback.string2SimpleCallbackContent data
            let state = Runtime.getState from.Id
            match (content, state) with
            | Some callbackData, Some state ->
                if callbackData = expectedCallback && state = expectedState
                then Some from
                else None
            | _ -> None
        | _ -> None

    let matchStateWithTextMessage expectedState ctx =
        let message = ctx.Update.Message
        match message with
        | Some {Chat = chat; Text = Some text } ->
            match Runtime.getState chat.Id with
            | Some state -> if state = expectedState then Some (chat, text) else None
            | _ -> None
        | _ -> None

    // Handler processing
    type HandlingResult =
        | Success
        | Fail

    type Handler = UpdateContext -> HandlingResult

    let rec processHandlers (handlers: Handler list) ctx =
        match handlers with
        | h::hs ->
            match h ctx with
            | Success -> Success
            | Fail -> processHandlers hs ctx
        | [] -> Fail

    let private processResultWithValue (result: Result<'a, ApiResponseError>) =
      match result with
      | Ok v -> Some v
      | Error e ->
        Logging.logError "Telegram server error: %s" e.Description
        None


    let private processResult (result: Result<'a, ApiResponseError>) = processResultWithValue result |> ignore
    let private botResult config data = api config data |> Async.RunSynchronously
    let private bot config data = botResult config data |> processResult

    let sendMessage (chatId: int64) (text, parseMode) photo replyMarkup config =
        match (photo, replyMarkup) with
        | Some content, Some markup ->
            Req.SendPhoto.Make(chatId, content, caption = text, parseMode = parseMode, replyMarkup = markup) |> bot config
        | Some content, None ->
            Req.SendPhoto.Make(chatId, content, caption = text, parseMode = parseMode) |> bot config
        | None, Some markup -> Req.SendMessage.Make(ChatId.Int chatId, text, replyMarkup = markup) |> bot config
        | None, None -> Req.SendMessage.Make(ChatId.Int chatId, text, parseMode = parseMode) |> bot config

    // Keyboard interaction
    let private sendMessageMarkup text replyMarkup config (chatId: int64) =
        sendMessage chatId (text, ParseMode.Markdown) None (Some replyMarkup) config

    let createInlineKeyboard arr =
        Array.map (
            Array.map (fun (text, callbackData) -> InlineKeyboardButton.Create(text, callbackData = Callback.callbackContent2String callbackData))
        ) arr |> InlineKeyboardMarkup.Create |> Markup.InlineKeyboardMarkup

    let sendInlineKeyboard text chatId keyboard config =
        let markup = Markup.InlineKeyboardMarkup { InlineKeyboard = keyboard }
        sendMessageMarkup text markup config chatId

    let sendReplyKeyboard text chatId keyboard config =
        let markup = Markup.ReplyKeyboardMarkup (ReplyKeyboardMarkup.Create(keyboard))
        sendMessageMarkup text markup config chatId

    let testRemoveKeyboard text chatId config  =
        let markup = Markup.ReplyKeyboardRemove { RemoveKeyboard = true; Selective = None; }
        sendMessageMarkup text markup config chatId

    // Text message interaction
    let private sendMessageFormatted text parseMode chatId config  =
      sendMessage chatId (text, parseMode) None None config
    let sendMarkdown text = sendMessageFormatted text ParseMode.Markdown
    let sendHtml text = sendMessageFormatted text ParseMode.HTML

    // Logging
    let logMessage handlerName userId message =
        Logging.logInfo $"Handler[{handlerName}] process message[{message}] from user[{userId}]"

    let logCallback handlerName userId callbackData =
        Logging.logInfo $"Handler[{handlerName}] process callback[{callbackData}] from user[{userId}]"

    let logAccessDenied handlerName userId =
        Logging.logInfo $"Access to handler[{handlerName}] denied for user[{userId}]"

    // Error handling
    let defaultHandleQueryingError err =
        match err with
        | Querying.ApiError ->
            "Мы не можем обработать ваш запрос, так как TonApi в данный момент не работает, попробуйте позже("
        | err ->
            Logging.logError $"Unexpected error {err}"
            "Если вы видите это сообщение значит что-то пошло не так"

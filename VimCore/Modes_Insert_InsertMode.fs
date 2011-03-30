﻿#light

namespace Vim.Modes.Insert
open Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor
open System

type CommandFunction = unit -> ProcessResult

/// This is information describing a particular TextEdit which was done 
/// to the ITextBuffer.  
[<RequireQualifiedAccess>]
type TextEdit =

    /// A character was inserted into the ITextBuffer
    | InsertChar of char

    /// A character was replaced in the ITextBuffer.  The first char is the
    /// original and the second is the new value
    | ReplaceChar of char * char

    /// A newline was inserted into the ITextBuffer
    | NewLine

    /// An unknown edit operation occurred.  Happens when actions like a
    /// normal mode command is run.  This breaks the ability for certain
    /// operations like replace mode to do a back space properly
    | UnknownEdit 

/// Data relating to a particular Insert mode session
type InsertSessionData = {

    /// The transaction which is bracketing this Insert mode session
    Transaction : ILinkedUndoTransaction option

    /// If this Insert is a repeat operation this holds the count and 
    /// whether or not a newline should be inserted after the text
    RepeatData : (int * bool) option

    /// The set of edit's which have occurred in this session
    TextEditList : TextEdit list
} with

    member x.AddTextEdit edit = { x with TextEditList = edit::x.TextEditList }



type internal InsertMode
    ( 
        _buffer : IVimBuffer, 
        _operations : Modes.ICommonOperations,
        _broker : IDisplayWindowBroker, 
        _editorOptions : IEditorOptions,
        _undoRedoOperations : IUndoRedoOperations,
        _textChangeTracker : ITextChangeTracker,
        _isReplace : bool ) as this =

    let _textView = _buffer.TextView
    let _editorOperations = _operations.EditorOperations
    let mutable _commandMap : Map<KeyInput, CommandFunction> = Map.empty
    let mutable _processTextInputCount = 0
    let _emptySessionData = {
        Transaction = None
        RepeatData = None
        TextEditList = List.empty
    }
    let mutable _sessionData = _emptySessionData

    do
        let commands : (string * CommandFunction) list = 
            [
                ("<Down>", this.ProcessDown)
                ("<Esc>", this.ProcessEscape)
                ("<Insert>", this.ProcessInsert)
                ("<Left>", this.ProcessLeft)
                ("<Right>", this.ProcessRight)
                ("<Up>", this.ProcessUp)
                ("<C-d>", this.ProcessShiftLeft)
                ("<C-t>", this.ProcessShiftRight)
                ("<C-o>", this.ProcessNormalModeOneCommand)
            ]

        _commandMap <-
            commands 
            |> Seq.ofList
            |> Seq.map (fun (str, func) -> (KeyNotationUtil.StringToKeyInput str), func)
            |> Map.ofSeq

    member x.IsProcessingTextInput = _processTextInputCount > 0

    member x.ModeKind = if _isReplace then ModeKind.Replace else ModeKind.Insert

    /// Is this KeyInput a raw text input item.  Really anything is text input except 
    /// for a few specific items
    member x.IsTextInput (ki : KeyInput) = 
        if Map.tryFind ki _commandMap |> Option.isSome then
            // Known commands are not text input
            false
        else
            match ki.Key with
            | VimKey.Enter -> true
            | VimKey.Back -> true
            | VimKey.Delete -> true
            | _ -> Option.isSome ki.RawChar

    /// Process the TextInput value
    member x.ProcessTextInput (ki : KeyInput) = 

        // Actually process the edit
        let processReplaceEdit () =
            let sessionData = _sessionData
            match ki.Key with
            | VimKey.Enter -> 
                let sessionData = sessionData.AddTextEdit TextEdit.NewLine
                _editorOperations.InsertNewLine(), sessionData
            | VimKey.Back ->
                // In replace we only support a backspace if the TextEdit stack is not
                // empty and points to something we can handle 
                match sessionData.TextEditList with 
                | [] ->
                    // Even though we take no action here we handled the KeyInput
                    true, sessionData
                | h::t ->
                    match h with 
                    | TextEdit.InsertChar _ -> 
                        let sessionData = { sessionData with TextEditList = t }
                        _editorOperations.Delete(), sessionData
                    | TextEdit.ReplaceChar (oldChar, newChar) ->
                        let point = 
                            let point = TextViewUtil.GetCaretPoint _textView 
                            SnapshotPointUtil.GetPreviousPointOnLine point 1
                        let span = Span(point.Position, 1)
                        let sessionData = { sessionData with TextEditList = t }
                        let result = _editorOperations.ReplaceText(span, (oldChar.ToString()))

                        // If the replace succeeded we need to position the caret back at the 
                        // start of the replace
                        if result then
                            TextViewUtil.MoveCaretToPosition _textView point.Position

                        result, sessionData
                    | TextEdit.NewLine -> 
                        true, sessionData
                    | TextEdit.UnknownEdit ->
                        true, sessionData
            | VimKey.Delete ->
                // Strangely a delete in replace actually does a delete but doesn't affect 
                // the edit stack
                _editorOperations.Delete(), sessionData
            | _ ->
                let text = ki.Char.ToString()
                let caretPoint = TextViewUtil.GetCaretPoint _textView
                let edit = 
                    if SnapshotPointUtil.IsInsideLineBreak caretPoint then
                        TextEdit.InsertChar ki.Char
                    else
                        TextEdit.ReplaceChar ((caretPoint.GetChar()), ki.Char)
                let sessionData = sessionData.AddTextEdit edit
                _editorOperations.InsertText(text), sessionData

        let processInsertEdit () =
            match ki.Key with
            | VimKey.Enter -> 
                _editorOperations.InsertNewLine()
            | VimKey.Back -> 
                _editorOperations.Backspace()
            | VimKey.Delete -> 
                _editorOperations.Delete()
            | _ ->
                let text = ki.Char.ToString()
                _editorOperations.InsertText(text)

        _processTextInputCount <- _processTextInputCount + 1
        try
            let value, sessionData =
                if _isReplace then 
                    processReplaceEdit()
                else
                    processInsertEdit(), _sessionData

            // If the edit succeeded then record the new InsertSessionData
            if value then
                _sessionData <- sessionData

            if value then
                ProcessResult.Handled ModeSwitch.NoSwitch
            else
                ProcessResult.NotHandled
         finally 
            _processTextInputCount <- _processTextInputCount - 1

    /// Process the up command
    member x.ProcessUp () =
        _operations.MoveCaretUp 1
        ProcessResult.Handled ModeSwitch.NoSwitch

    /// Process the down command
    member x.ProcessDown () =
        _operations.MoveCaretDown 1
        ProcessResult.Handled ModeSwitch.NoSwitch

    /// Process the left command
    member x.ProcessLeft () =
        _operations.MoveCaretLeft 1
        ProcessResult.Handled ModeSwitch.NoSwitch

    /// Process the right command
    member x.ProcessRight () =
        _operations.MoveCaretRight 1
        ProcessResult.Handled ModeSwitch.NoSwitch

    /// Process the <Insert> command.  This toggles between insert an replace mode
    member x.ProcessInsert () = 

        let mode = if _isReplace then ModeKind.Insert else ModeKind.Replace
        ProcessResult.Handled (ModeSwitch.SwitchMode mode)

    /// Enter normal mode for a single command.  
    member x.ProcessNormalModeOneCommand () =

        // If we're in replace mode then this will be a blocking edit.  Record it
        if _isReplace then
            _sessionData <- _sessionData.AddTextEdit TextEdit.UnknownEdit

        let switch = ModeSwitch.SwitchModeWithArgument (ModeKind.Normal, ModeArgument.OneTimeCommand x.ModeKind)
        ProcessResult.Handled switch

    /// Process the CTRL-D combination and do a shift left
    member x.ProcessShiftLeft() = 
        let range = TextViewUtil.GetCaretLineRange _textView 1
        _operations.ShiftLineRangeLeft range 1
        ProcessResult.Handled ModeSwitch.NoSwitch

    /// Process the CTRL-T combination and do a shift right
    member x.ProcessShiftRight () = 
        let range = TextViewUtil.GetCaretLineRange _textView 1
        _operations.ShiftLineRangeRight range 1
        ProcessResult.Handled ModeSwitch.NoSwitch

    /// Apply the repeated edits the the ITextBuffer
    member x.MaybeApplyRepeatedEdits () = 

        // Function to actually repeat the edits
        let repeatEdits count addNewLines change = 
            match change with
            | TextChange.Insert text -> 
                // Insert the same text 'count - 1' times at the cursor
                let text = 
                    if addNewLines then
                        let text = Environment.NewLine + text
                        StringUtil.repeat (count - 1) text
                    else 
                        StringUtil.repeat (count - 1) text

                let caretPoint = TextViewUtil.GetCaretPoint _textView
                let span = SnapshotSpan(caretPoint, 0)
                let snapshot = _textView.TextBuffer.Replace(span.Span, text) |> ignore

                // Now make sure to position the caret at the end of the inserted
                // text
                TextViewUtil.MoveCaretToPosition _textView (caretPoint.Position + text.Length)
            | TextChange.Delete deleteCount -> 
                // Delete '(count - 1) * deleteCount' more characters
                let caretPoint = TextViewUtil.GetCaretPoint _textView
                let count = deleteCount * (count - 1)
                let count = min (_textView.TextSnapshot.Length - caretPoint.Position) count
                _textView.TextBuffer.Delete((Span(caretPoint.Position, count))) |> ignore

                // Now make sure the caret is still at the same position
                TextViewUtil.MoveCaretToPosition _textView caretPoint.Position

        try
            match _sessionData.RepeatData, _textChangeTracker.CurrentChange with
            | None, None -> ()
            | None, Some _ -> ()
            | Some _, None -> ()
            | Some (count, addNewLines), Some change -> repeatEdits count addNewLines change
        finally
            // Make sure to close out the transaction
            match _sessionData.Transaction with
            | None -> 
                ()
            | Some transaction -> 
                transaction.Complete()
                _sessionData <- { _sessionData with Transaction = None }

    member x.ProcessEscape () =

        this.MaybeApplyRepeatedEdits()

        if _broker.IsCompletionActive || _broker.IsSignatureHelpActive || _broker.IsQuickInfoActive then
            _broker.DismissDisplayWindows()
            _operations.MoveCaretLeft 1 
            ProcessResult.OfModeKind ModeKind.Normal

        else
            // Need to adjust the caret on exit.  Typically it's just a move left by 1 but if we're
            // in virtual space we just need to get out of it.
            let virtualPoint = TextViewUtil.GetCaretVirtualPoint _textView
            if virtualPoint.IsInVirtualSpace then 
                _operations.MoveCaretToPoint virtualPoint.Position
            else
                _operations.MoveCaretLeft 1 
            ProcessResult.OfModeKind ModeKind.Normal

    /// Can Insert mode handle this particular KeyInput value 
    member x.CanProcess ki = 
        if Map.containsKey ki _commandMap then
            true
        else
            x.IsTextInput ki

    /// Process the KeyInput
    member x.Process ki = 
        match Map.tryFind ki _commandMap with
        | Some(func) -> 
            func()
        | None -> 
            if x.IsTextInput ki then 
                x.ProcessTextInput ki
            else
                ProcessResult.NotHandled

    /// Entering an insert or replace mode.  Setup the InsertSessionData based on the 
    /// ModeArgument value. 
    member x.OnEnter arg =

        // On enter we need to check the 'count' and possibly set up a transaction to 
        // lump edits and their repeats together
        let transaction, repeatData =
            match arg with
            | ModeArgument.InsertWithCount count ->
                if count > 1 then
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, Some (count, false)
                else
                    None, None
            | ModeArgument.InsertWithCountAndNewLine count ->
                if count > 1 then
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, Some (count, true)
                else
                    None, None
            | ModeArgument.InsertWithTransaction transaction ->
                Some transaction, None
            | _ -> 
                if _isReplace then
                    // Replace mode occurs under a transaction even if we are not repeating
                    let transaction = _undoRedoOperations.CreateLinkedUndoTransaction()
                    Some transaction, None
                else
                    None, None

        _sessionData <- {
            Transaction = transaction
            RepeatData = repeatData
            TextEditList = List.empty
        }

        // If this is replace mode then go ahead and setup overwrite
        if _isReplace then
            _editorOptions.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, true)

    /// Called when leaving insert mode.  Here we will do any remaining cleanup on the
    /// InsertSessionData.  It's possible to get here with active session data if there
    /// is an exception during the processing of the transaction.
    ///
    /// Or more sinister.  A simple API call to OnLeave could force us to leave while 
    /// a transaction was open
    member x.OnLeave () =

        try
            match _sessionData.Transaction with
            | None -> ()
            | Some transaction -> transaction.Complete()
        finally
            _sessionData <- _emptySessionData

        // If this is replace mode then go ahead and undo overwrite
        if _isReplace then
            _editorOptions.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, false)

    interface IInsertMode with 
        member x.VimBuffer = _buffer
        member x.CommandNames =  _commandMap |> Seq.map (fun p -> p.Key) |> Seq.map OneKeyInput
        member x.ModeKind = x.ModeKind
        member x.IsProcessingTextInput = x.IsProcessingTextInput
        member x.CanProcess ki = x.CanProcess ki
        member x.IsTextInput ki = x.IsTextInput ki
        member x.Process ki = x.Process ki
        member x.OnEnter arg = x.OnEnter arg
        member x.OnLeave () = x.OnLeave ()
        member x.OnClose() = ()


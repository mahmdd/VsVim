﻿#light

namespace VimCore.Modes.Normal
open VimCore
open VimCore.Modes.Common
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open System.Windows.Input
open System.Windows.Media

module internal Operations =

    /// Process the m[a-z] command.  Called when the m has been input so wait for the next key
    let Mark (d:NormalModeData) =
        let waitForKey (d2:NormalModeData) (ki:KeyInput) =
            let bufferData = d2.VimBufferData
            let cursor = ViewUtil.GetCaretPoint bufferData.TextView
            let res = Modes.Common.Operations.SetMark bufferData.MarkMap cursor ki.Char
            match res with
            | Operations.Failed(_) -> bufferData.VimHost.Beep()
            | _ -> ()
            NormalModeResult.Complete
        NormalModeResult.NeedMore2 waitForKey

    /// Process the ' or ` jump to mark keys
    let JumpToMark (d:NormalModeData) =
        let waitForKey (d:NormalModeData) (ki:KeyInput) =
            let bufferData = d.VimBufferData
            let res = Modes.Common.Operations.JumpToMark bufferData.MarkMap bufferData.TextView ki.Char
            match res with 
            | Operations.Failed(msg) -> bufferData.VimHost.UpdateStatus(msg)
            | _ -> ()
            NormalModeResult.Complete
        NormalModeResult.NeedMore2 waitForKey
            

module Client.Tests.TerminalMouseTests

open System
open Client.TerminalMouse
open NUnit.Framework

let private key ch =
    ConsoleKeyInfo(ch, (if ch = '\u001b' then ConsoleKey.Escape else ConsoleKey.NoName), false, false, false)

let private feed (decoder: Decoder) (text: string) =
    text |> Seq.collect (fun ch -> decoder.Feed(DateTimeOffset.UtcNow, key ch)) |> Seq.toList

let private equalEvents (expected: InputEvent list) (actual: InputEvent list) =
    Assert.That(box actual, Is.EqualTo(box expected))

[<Test>]
let sgrLeftClicksUseZeroBasedCoordinates () =
    let decoder = Decoder()
    equalEvents [Mouse(Click(11, 6))] (feed decoder "\u001b[<0;12;7M")
    Assert.That(box (feed decoder "\u001b[<0;1;1m"), Is.Empty)

[<Test>]
let wheelAndModifierReportsAreHandled () =
    let decoder = Decoder()
    equalEvents [Mouse(Scroll(-1, 30, 4))] (feed decoder "\u001b[<64;31;5M")
    equalEvents [Mouse(Scroll(1, 30, 4))] (feed decoder "\u001b[<65;31;5M")
    equalEvents [Mouse(Click(1, 1))] (feed decoder "\u001b[<4;2;2M")
    Assert.That(box (feed decoder "\u001b[<32;2;2M"), Is.Empty)

[<Test>]
let invalidOrUnrelatedReportsCannotTriggerMouseActions () =
    let decoder = Decoder()
    Assert.That(box (feed decoder "\u001b[<2;6;3M"), Is.Empty)
    Assert.That(box (feed decoder "\u001b[<0;0;1M"), Is.Empty)
    Assert.That(box (feed decoder "\u001b[<0;99999999;1M"), Is.Empty)
    Assert.That(box (feed decoder "\u001b[<0;2;2m"), Is.Empty)

[<Test>]
let standaloneEscapeIsNotLost () =
    let decoder = Decoder()
    let now = DateTimeOffset.UtcNow
    Assert.That(box (decoder.Feed(now, key '\u001b')), Is.Empty)
    Assert.That(box (decoder.Flush(now.AddMilliseconds(50.))), Is.Empty)
    equalEvents [Key(key '\u001b')] (decoder.Flush(now.AddMilliseconds(70.)))

[<Test>]
let ordinaryKeysAndRawArrowSequencesRemainUsable () =
    let decoder = Decoder()
    equalEvents [Key(key 'q')] (decoder.Feed(DateTimeOffset.UtcNow, key 'q'))
    match feed decoder "\u001b[A" with
    | [Key info] -> Assert.That(info.Key, Is.EqualTo(ConsoleKey.UpArrow))
    | _ -> Assert.Fail("Arrow was interpreted as mouse input.")

[<Test>]
let malformedSequenceIsBoundedAndDecoderRecovers () =
    let decoder = Decoder()
    let malformed = "\u001b[<" + String.replicate 80 "1"
    let observed = feed decoder malformed
    Assert.That(observed |> List.exists (function Mouse _ -> true | _ -> false), Is.False)
    equalEvents [Key(key 'x')] (decoder.Feed(DateTimeOffset.UtcNow, key 'x'))

[<Test>]
let adjacentWheelEventsCombineAndPreserveNavigationBoundaries () =
    let key = Key(ConsoleKeyInfo('x', ConsoleKey.X, false, false, false))
    let result =
        coalesceWheelEvents
            [ Mouse(Scroll(-1, 1, 2))
              Mouse(Scroll(-1, 2, 3))
              Mouse(Scroll(-1, 3, 4))
              key
              Mouse(Scroll(1, 8, 9))
              Mouse(Click(5, 6))
              Mouse(Scroll(-1, 7, 8)) ]
    Assert.That(result, Is.EqualTo(
        [ Mouse(Scroll(-3, 3, 4))
          key
          Mouse(Scroll(1, 8, 9))
          Mouse(Click(5, 6))
          Mouse(Scroll(-1, 7, 8)) ] :> obj))

[<Test>]
let alternatingWheelDeltasCancelWithoutSkippingKeyEvents () =
    let key = Key(ConsoleKeyInfo('a', ConsoleKey.A, false, false, false))
    Assert.That(
        coalesceWheelEvents
            [ Mouse(Scroll(1, 0, 0)); Mouse(Scroll(-1, 0, 0)); key;
              Mouse(Scroll(1, 1, 1)); Mouse(Scroll(1, 1, 1)) ],
        Is.EqualTo([ key; Mouse(Scroll(2, 1, 1)) ] :> obj))

[<Test>]
let largeMouseWheelBurstCollapsesToOneEvent () =
    let burst = List.replicate 512 (Mouse(Scroll(-1, 1, 1)))
    Assert.That(coalesceWheelEvents burst,
                Is.EqualTo([ Mouse(Scroll(-512, 1, 1)) ] :> obj))

# Unofficial .NET client for Prove

You can use this from C# or F#.

https://www.prove.com/

Supported services:

- Identity (IDV)
- Pre-Fill
- TrustScore
- Sms Delivery
- InstantLink SMS

## Usage examples ##

Here are some examples how to use this library.
The test data here (e.g. phone "2001001686") is not real, just Prove test-environment data.
For simplicity we use F#.

### Identity (IDV) ###

With Identity you send a customer information to Prove and they verify if that customer is match in their record.

```fsharp
/// ProveSDK.ServiceLicense: Get your license details from Prove
let license = { Environment = ProveEnvironment.Staging; ProveUser = "serviceuser"; ProvePassword = "pwd"; ClientId = "123"}
let testVerifyProve1() = ProveSDK.Identity.callProveVerify license "2001001686" "Tod" "Weedall" (Some "San Antonio") (Some "78285") (Some "TX") (Some (DateTime(1984,12,10))) (Some "565-22-8370") |> Async.RunSynchronously
let testVerifyProve2() = ProveSDK.Identity.callProveVerify license "2001001687" "Agretha" "Chene" (Some "Boston") (Some "2208") (Some "MA") (Some (DateTime(1994,12,29))) (Some "369-95-6933") |> Async.RunSynchronously
```

### TrustScore ###

Trustscore gives a trust-score (from 0-1000) of how reliable the phone number is.

```fsharp
let license = { Environment = ProveEnvironment.Staging; ProveUser = "serviceuser"; ProvePassword = "pwd"; ClientId = "123"}
let testTrustScoreProve1() = ProveSDK.callProveTrustScore license "2001001686" |> Async.RunSynchronously //Some 925
```

### Pre-Fill ###

Prefill needs a verified phone number and consent from customer. It fetches data of a customer based on a few key factors.

```fsharp
let license = { Environment = ProveEnvironment.Staging; ProveUser = "serviceuser"; ProvePassword = "pwd"; ClientId = "123"}
let testIdentityProve1() = ProveSDK.provePrefillIdentity license "2001001686" (DateTime(1984,12,10)) (Some "565-22-8370") |> Async.RunSynchronously
```

### SMS Delivery ###

You can send SMS text messages via Prove, if you have that service enabled in your contract.

```fsharp
let smsLicense = { Environment = ProveEnvironment.Staging; ApiClientId = "123"; SubClientId = Some "456" }
ProveSDK.Sms.send smsLicense "2001001686" "Hello World."
```

### InstantLink ###

InstantLink is a phone verification service. It sends a code to customer and expects to validate it.

```fsharp
let sesssionId = System.Guid.NewGuid().ToString()
let myReturnUrl = "http://localhost" //your service return
let instaLicense =  { Environment = ProveEnvironment.Staging; ApiClientId = "123"; SubClientId = Some "123" }

let! genResult = ProveSDK.InstantLink.generate instaLicense myReturnUrl "123.123.123.123" "2001001686" sessionId
match genResult with
| Ok (InstantLink.InstantLink instantLink) ->
   // send instantLink to user via sms (see above) 
   instantLink
| _ -> ""

let fingerPring = ProveSDK.InstantLink.validate instaLicense sessionId verificationFingerprint
let result =
   match fingerPring with
   | Ok res when res.LinkClicked -> true
   | _ -> false

```

Nuget: https://www.nuget.org/packages/ProveSDK/

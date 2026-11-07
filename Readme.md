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

Identity, TrustScore and Pre-Fill take a `ConsentStatus`: say what consent you hold from the person (`OptedIn`, `OptedOut`, `NotCollected` or `Unknown`).
They refuse a phone number that starts with a trunk `0` or has one in brackets, such as `07700 900123` or `+44 (0)7700 900123`, as the number sent would be a different one: write `+44 7700 900123`.
After `+44` or `0044`, a trunk `0` without brackets is dropped, as E.164 says for the UK: `+44 07700 900123` goes as `447700900123`. What follows must be 7 to 10 digits. `44 07700 900123`, without `+` or `00`, is refused.
After any other country code every digit goes as typed, so write those numbers in E.164: Italy's `+39 06` keeps its zero, and this library has no rule for countries that drop theirs.
Sms and InstantLink refuse every number the other three refuse. One they accept goes as typed, except the two UK forms above, which go as `+447700900123`.
The failure for a refused number gives how many digits it holds and at most its last two.

A 2xx answer whose body can't be read, an InstantLink answer without a `status`, and an Identity answer whose `response` has no readable `verified` come back as each call's own failure, never an exception or a success. The failure holds the status and the body's length and kind, nothing from the body.

The failure for a non-2xx answer also holds these fields when the body is a JSON object: `status`, `code`, `failureCode`, `responseCode`, `error`, `description`, `message`, `additionalInfo` and `error_description`. Email addresses and digits in the worded ones are masked; a name in them can't be recognised. Other fields and a body that isn't a JSON object stay out.

### Identity (IDV) ###

With Identity you send a customer information to Prove and they verify if that customer is match in their record.

```fsharp
/// ProveSDK.ServiceLicense: Get your license details from Prove
let license = { Environment = ProveEnvironment.Staging; ProveUser = "serviceuser"; ProvePassword = "pwd"; ClientId = "123"}
let testVerifyProve1() = ProveSDK.Identity.callProveVerify license ConsentStatus.OptedIn "2001001686" "Tod" "Weedall" (Some "San Antonio") (Some "78285") (Some "TX") (Some (DateTime(1984,12,10))) (Some "565-22-8370") |> Async.RunSynchronously
let testVerifyProve2() = ProveSDK.Identity.callProveVerify license ConsentStatus.OptedIn "2001001687" "Agretha" "Chene" (Some "Boston") (Some "2208") (Some "MA") (Some (DateTime(1994,12,29))) (Some "369-95-6933") |> Async.RunSynchronously
```

### TrustScore ###

Trustscore gives a trust-score (from 0-1000) of how reliable the phone number is.

```fsharp
let license = { Environment = ProveEnvironment.Staging; ProveUser = "serviceuser"; ProvePassword = "pwd"; ClientId = "123"}
let testTrustScoreProve1() = ProveSDK.TrustScore.callProveTrustScore license ConsentStatus.OptedIn "2001001686" |> Async.RunSynchronously //Some 925
```

### Pre-Fill ###

Prefill needs a verified phone number and consent from customer. It fetches data of a customer based on a few key factors.

```fsharp
let license = { Environment = ProveEnvironment.Staging; ProveUser = "serviceuser"; ProvePassword = "pwd"; ClientId = "123"}
let testIdentityProve1() = ProveSDK.Prefill.provePrefillIdentity license ConsentStatus.OptedIn "2001001686" (DateTime(1984,12,10)) (Some "565-22-8370") |> Async.RunSynchronously
```

### SMS Delivery ###

You can send SMS text messages via Prove, if you have that service enabled in your contract.

```fsharp
let smsLicense : ProveSDK.Sms.License = { Environment = ProveEnvironment.Staging; ClientId = "123"; LicenseKey = "456" }
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

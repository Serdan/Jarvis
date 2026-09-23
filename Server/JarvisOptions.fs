namespace Server

[<CLIMutable>]
type JarvisOptions =
    { Auth0Domain: string
      Audience: string
      OpenAIAppsChallenge: string }

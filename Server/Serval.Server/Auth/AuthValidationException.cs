namespace Serval.Server.Auth;

/// <summary>Bad input to an auth operation (weak password, malformed username, duplicate
/// account).</summary>
public sealed class AuthValidationException(string message) : ValidationException(message);

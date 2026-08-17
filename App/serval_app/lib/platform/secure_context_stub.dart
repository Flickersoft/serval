/// Off the web there is no such thing as an insecure context: the microphone is a platform
/// permission, not an origin one, and nothing here is gated on how the Server is reached.
bool get isSecureContext => true;

namespace Dexicon.Core.Documents;

/// <summary>
/// A stored document was deleted before it could be attached: the last corpus that held it detached it between
/// the lookup and the attachment. An <see cref="ArgumentException"/>, so a batch upload lists the file under
/// <c>failed</c> and goes on with the rest, and the attach endpoint answers 404 as for a hash no document has.
/// The message goes to the caller, so it names no parameter.
/// </summary>
public sealed class DocumentGoneException(string message) : ArgumentException(message);

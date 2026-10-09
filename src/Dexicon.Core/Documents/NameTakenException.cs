namespace Dexicon.Core.Documents;

/// <summary>
/// A document that is already attached to a corpus could not take the name asked for, because another
/// document in the corpus has it. An <see cref="ArgumentException"/>, so a batch upload lists the file
/// under <c>failed</c> and goes on with the rest, and the attach endpoint answers 409. The message goes to
/// the caller, so it names no parameter.
/// </summary>
public sealed class NameTakenException(string message) : ArgumentException(message);

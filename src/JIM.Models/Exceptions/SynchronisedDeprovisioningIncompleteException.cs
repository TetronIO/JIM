// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// A Synchronised Deprovisioning run processed every object it could but held some back, leaving them exactly as they
/// were because a surviving contributor's scope could not be decided. The Connected System is therefore not deleted
/// and stays fenced; deleting it again, once the cause is corrected, starts the run over and finishes it.
/// </summary>
public class SynchronisedDeprovisioningIncompleteException : OperationalException
{
    /// <param name="connectedSystemName">The Connected System being deleted.</param>
    /// <param name="heldBackCount">How many of its Connected System Objects were held back.</param>
    public SynchronisedDeprovisioningIncompleteException(string connectedSystemName, int heldBackCount)
        : base($"Connected System '{connectedSystemName}' was not deleted: {heldBackCount:N0} of its Connected System Object(s) were " +
               "left as they were, with their Metaverse Objects, because a scoping criterion could not be evaluated for a " +
               "surviving contributor. The errors on this Activity name each object and criterion. Correct the criterion, " +
               "then delete the Connected System again to finish.")
    {
        HeldBackCount = heldBackCount;
    }

    /// <summary>How many Connected System Objects were held back.</summary>
    public int HeldBackCount { get; }
}

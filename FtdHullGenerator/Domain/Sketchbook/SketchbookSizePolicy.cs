namespace FtdHullGenerator.Domain.Sketchbook;

/// <summary>How an applied sketchbook entry chooses hull dimensions.</summary>
public enum SketchbookSizePolicy
{
    /// <summary>Use the entry's exact suggested length, width and height and its exact baseline rises.</summary>
    SuggestedDimensions,

    /// <summary>Retain the current length, width and height and scale each baseline rise from the immutable baseline.</summary>
    KeepCurrentDimensions,
}

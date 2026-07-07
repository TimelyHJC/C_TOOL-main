using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace C_toolsDddPlugin;

internal static class DddDimensionSelectionFilter
{
    internal static void AddLinearAlignedAllowedClasses(PromptEntityOptions options)
    {
        options.AddAllowedClass(typeof(RotatedDimension), true);
        options.AddAllowedClass(typeof(AlignedDimension), true);
    }

    internal static bool IsRotatedDimension(ObjectId objectId) =>
        IsValidObjectId(objectId) && IsRotatedDimensionClass(objectId.ObjectClass);

    internal static bool IsLinearOrAlignedDimension(ObjectId objectId) =>
        IsValidObjectId(objectId) && IsLinearOrAlignedDimensionClass(objectId.ObjectClass);

    internal static bool IsDimension(ObjectId objectId) =>
        IsValidObjectId(objectId) && IsDimensionClass(objectId.ObjectClass);

    internal static bool IsRotatedDimensionClass(Autodesk.AutoCAD.Runtime.RXClass? objectClass)
    {
        if (objectClass == null)
            return false;

        var rotatedClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(RotatedDimension));
        return objectClass.IsDerivedFrom(rotatedClass);
    }

    internal static bool IsLinearOrAlignedDimensionClass(Autodesk.AutoCAD.Runtime.RXClass? objectClass)
    {
        if (objectClass == null)
            return false;

        var rotatedClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(RotatedDimension));
        var alignedClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(AlignedDimension));
        return objectClass.IsDerivedFrom(rotatedClass) || objectClass.IsDerivedFrom(alignedClass);
    }

    internal static bool IsDimensionClass(Autodesk.AutoCAD.Runtime.RXClass? objectClass)
    {
        if (objectClass == null)
            return false;

        var dimensionClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(Dimension));
        return objectClass.IsDerivedFrom(dimensionClass);
    }

    private static bool IsValidObjectId(ObjectId objectId) =>
        objectId.IsValid && !objectId.IsErased;
}

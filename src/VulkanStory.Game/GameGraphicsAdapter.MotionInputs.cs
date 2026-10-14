namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private readonly Dictionary<int, (int Model, int Wind, int Water)> motionInputLocations = new();
    private (int Model, int Wind, int Water) MotionInputLocations(int program)
    {
        if (!motionInputLocations.TryGetValue(program, out var locations))
        {
            var renderer = RequireDevice();
            locations = (renderer.GetUniformLocation(program, "modelMatrix"),
                renderer.GetUniformLocation(program, "windWaveIntensity"),
                renderer.GetUniformLocation(program, "waterWaveCounter"));
            motionInputLocations.Add(program, locations);
        }
        return locations;
    }
    private void CaptureMotionModel(int program, int location, ReadOnlySpan<float> values)
    {
        if (location >= 0 && aoTemporal?.EntityMotion.Enabled == true && MotionInputLocations(program).Model == location)
            aoTemporal.EntityMotion.NoteModelMatrix(program, values);
    }
    private void InvalidateMotionInput(int program, int location)
    {
        if (location < 0 || aoTemporal?.EntityMotion.Enabled != true) return;
        var locations = MotionInputLocations(program);
        if (locations.Model == location) aoTemporal.EntityMotion.InvalidateModel(program);
        else if (locations.Wind == location || locations.Water == location) aoTemporal.EntityMotion.InvalidateUpload(program);
    }
}

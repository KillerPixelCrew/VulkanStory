using VulkanStory.Render.Vulkan.Shaders;

// Top-level shader CLI delegates argument handling, compiler output, and exit status
// to NativeShaderTool; generated shader artifacts do not establish visual acceptance.
return NativeShaderTool.Run(args, Console.Out, Console.Error);

# Rebuilds package/WolfPets/WolfPets.pck from source/ with the Godot 3.5 engine's own packer.
# Run from this folder with:  godot --no-window -s pack.gd
extends SceneTree

func _init():
	var out = OS.get_environment("PCK_OUT")
	if out == "":
		out = "package/WolfPets/WolfPets.pck"
	var src = OS.get_environment("PCK_SRC_DIR")
	if src == "":
		src = "source/mods/WolfPets"
	var packer = PCKPacker.new()
	packer.pck_start(out, 32)
	packer.add_file("res://mods/WolfPets/main.gd", src.plus_file("main.gd"))
	packer.add_file("res://mods/WolfPets/wolf_pet.scn", src.plus_file("wolf_pet.scn"))
	packer.add_file("res://mods/WolfPets/pet_area.gd", src.plus_file("pet_area.gd"))
	print("pack result: ", packer.flush(true))
	quit()

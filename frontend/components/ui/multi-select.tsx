"use client";

import * as React from "react";
import { Check, ChevronsUpDown, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
    Command,
    CommandEmpty,
    CommandGroup,
    CommandInput,
    CommandItem,
    CommandList,
} from "@/components/ui/command";
import {
    Popover,
    PopoverContent,
    PopoverTrigger,
} from "@/components/ui/popover";

interface MultiSelectProps {
    options: string[];
    selected: string[];
    onChange: (selected: string[]) => void;
    placeholder?: string;
    searchPlaceholder?: string;
    emptyMessage?: string;
}

export function MultiSelect({ options, selected, onChange, placeholder = "Select options...", searchPlaceholder = "Search...", emptyMessage = "No results found." }: MultiSelectProps) {
    const [open, setOpen] = React.useState(false);

    const toggleOption = (option: string) => {
        const isSelected = selected.includes(option);
        if (isSelected) {
            onChange(selected.filter((item) => item !== option));
        } else {
            onChange([...selected, option]);
        }
    };

    const removeOption = (option: string, e: React.MouseEvent) => {
        e.stopPropagation();
        onChange(selected.filter((item) => item !== option));
    };

    return (
        <Popover open={open} onOpenChange={setOpen}>
            <PopoverTrigger asChild>
                <Button
                    variant="outline"
                    role="combobox"
                    aria-expanded={open}
                    className="w-full justify-between bg-background min-h-10 h-auto px-3 py-2"
                >
                    <div className="flex flex-wrap gap-1 w-full overflow-hidden text-left pl-1">
                        {selected.length === 0 ? (
                            <span className="text-muted-foreground font-normal line-clamp-1 py-0.5">{placeholder}</span>
                        ) : (
                            selected.map((item) => (
                                <Badge
                                    variant="secondary"
                                    key={item}
                                    className="mr-1 mb-1 font-normal animate-in zoom-in-75 duration-200"
                                >
                                    {item}
                                    <div
                                        role="button"
                                        tabIndex={0}
                                        className="ml-1 ring-offset-background rounded-full outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2 hover:bg-muted cursor-pointer"
                                        onKeyDown={(e) => {
                                            if (e.key === "Enter") {
                                                onChange(selected.filter((i) => i !== item));
                                            }
                                        }}
                                        onMouseDown={(e) => {
                                            e.preventDefault();
                                            e.stopPropagation();
                                        }}
                                        onClick={(e) => removeOption(item, e)}
                                    >
                                        <X className="h-3 w-3 text-muted-foreground hover:text-foreground" />
                                        <span className="sr-only">Remove {item}</span>
                                    </div>
                                </Badge>
                            ))
                        )}
                    </div>
                    <ChevronsUpDown className="h-4 w-4 shrink-0 opacity-50 ml-2" />
                </Button>
            </PopoverTrigger>
            <PopoverContent className="w-[300px] p-0" align="start">
                <Command>
                    <CommandInput placeholder={searchPlaceholder} />
                    <CommandList>
                        <CommandEmpty>{emptyMessage}</CommandEmpty>
                        <CommandGroup className="max-h-[300px] overflow-auto">
                            {options.map((option) => {
                                const isSelected = selected.includes(option);
                                return (
                                    <CommandItem
                                        key={option}
                                        onSelect={() => toggleOption(option)}
                                        className="cursor-pointer"
                                    >
                                        <div
                                            className={cn(
                                                "mr-2 flex h-4 w-4 items-center justify-center rounded-sm border border-primary",
                                                isSelected
                                                    ? "bg-primary text-primary-foreground"
                                                    : "opacity-50 [&_svg]:invisible"
                                            )}
                                        >
                                            <Check className={cn("h-4 w-4")} />
                                        </div>
                                        <span>{option}</span>
                                    </CommandItem>
                                );
                            })}
                        </CommandGroup>
                    </CommandList>
                </Command>
            </PopoverContent>
        </Popover>
    );
}
